#!/bin/bash
# Instella Server Deployment Script (Linux)
# Pulls (from a registry) or loads (from a tar file) a versioned image and starts/updates the stack

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Configuration
DEPLOY_DIR="${DEPLOY_DIR:-/opt/instella}"
BACKUP_DIR="${BACKUP_DIR:-$DEPLOY_DIR/backup}"
COMPOSE_FILE="docker-compose.yml"

usage() {
    echo "Usage: $0 <version> [options]"
    echo "       $0 --restore <backup-folder>"
    echo ""
    echo "Arguments:"
    echo "  version         Image tag to deploy (e.g., 0.1.0, latest, edge, sha-1a2b3c4)"
    echo ""
    echo "Options:"
    echo "  --pull          Pull the image from its registry (INSTELLA_IMAGE in .env, e.g. ghcr.io/djgosnell/instella-server)"
    echo "  --load          Load image from tar file (default: looks for instella-server-<version>.tar)"
    echo "  --no-load       Skip loading image (assume it's already available)"
    echo "  --no-backup     Skip backup before deployment"
    echo "  --restore NAME  Restore from a backup folder (e.g., instella-0.1.0-20260131_123456)"
    echo "  --dry-run       Show what would be done without executing"
    echo ""
    echo "Examples:"
    echo "  $0 0.1.0 --pull                               # Pull version 0.1.0 from the registry and deploy"
    echo "  $0 edge --pull                                # Pull the newest build of master and deploy"
    echo "  $0 0.1.0 --load                               # Load image from tar and deploy"
    echo "  $0 --restore instella-0.1.0-20260131_123456   # Restore from backup"
    exit 1
}

log_info() {
    echo -e "${CYAN}[INFO]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[OK]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# The container runs as uid/gid 1654 (the .NET image's "app" user). Creates config/ and
# packages/ and, as root, gives them to that user; otherwise prints the command to run.
prepare_volumes() {
    local dir
    for dir in config packages; do
        if [[ ! -d "$dir" ]]; then
            mkdir -p "$dir"
            log_info "Created $dir directory"
        fi
    done
    if [[ $EUID -eq 0 ]]; then
        chown -R 1654:1654 config packages
    elif [[ "$(stat -c '%u' config)" != "1654" || "$(stat -c '%u' packages)" != "1654" ]]; then
        log_warn "config/ and packages/ must belong to uid 1654 (the container's user). Run:"
        log_info "  sudo chown -R 1654:1654 config packages"
    fi
}

# Waits up to 90 s for the server container to report healthy. On failure prints the last
# health-check output; there is no automatic rollback (restore a backup with --restore).
wait_for_healthy() {
    local id status="starting" i
    id=$(docker compose -f "$COMPOSE_FILE" ps -q instella-server)
    if [[ -z "$id" ]]; then
        log_error "The instella-server container is not running"
        return 1
    fi
    for i in $(seq 1 45); do
        status=$(docker inspect --format '{{.State.Health.Status}}' "$id" 2>/dev/null || echo "missing")
        [[ "$status" == "healthy" ]] && return 0
        [[ "$status" == "unhealthy" ]] && break
        sleep 2
    done
    log_error "The server did not become healthy (status: $status). Last health check output:"
    docker inspect --format '{{range .State.Health.Log}}{{.Output}}{{end}}' "$id" 2>/dev/null | tail -n 5
    return 1
}

# Convert .env to Unix line endings if needed (handles Windows CRLF)
fix_env_line_endings() {
    if grep -q $'\r' .env 2>/dev/null; then
        log_warn "Converting .env to Unix line endings..."
        sed -i 's/\r$//' .env
    fi
}

# Safely read a value from .env (handles CRLF)
get_env_value() {
    local key="$1"
    grep -E "^${key}=" .env 2>/dev/null | cut -d= -f2 | tr -d '\r'
}

# Copy a directory preserving modes and ownership. Package blobs are content-addressed and
# never modified in place, so hard links (cp -al) snapshot them without doubling disk use;
# a plain copy is the fallback when the backup is on another file system.
copy_tree() {
    local src="$1" dest="$2" link="${3:-false}"
    if [[ "$link" == true ]] && cp -al "$src" "$dest" 2>/dev/null; then
        return 0
    fi
    rm -rf "$dest"
    cp -a "$src" "$dest"
}

# Create a complete backup (database, keys, config files and package blobs). The server is
# stopped first: copying a live SQLite database can capture a torn write.
backup_deployment() {
    local version="$1"
    local timestamp=$(date +%Y%m%d_%H%M%S)
    local backup_name="instella-${version}-${timestamp}"
    local backup_subdir="$BACKUP_DIR/$backup_name"

    # Create backup subdirectory
    mkdir -p "$backup_subdir"

    log_info "Creating backup: $backup_name"

    log_info "Stopping the server for a consistent copy..."
    docker compose -f "$COMPOSE_FILE" stop 2>/dev/null || true

    # Backup config files
    log_info "Backing up configuration files..."
    cp .env "$backup_subdir/.env" 2>/dev/null || true
    cp "$COMPOSE_FILE" "$backup_subdir/$COMPOSE_FILE"

    # Backup config directory (SQLite database, Data Protection keys, appsettings.json)
    if [[ -d "config" ]]; then
        copy_tree config "$backup_subdir/config"
        log_success "Config directory backed up"
    fi

    # Backup package blobs: the database refers to them, so they belong to the same snapshot
    if [[ -d "packages" ]]; then
        copy_tree packages "$backup_subdir/packages" true
        log_success "Package storage backed up"
    fi

    # Show backup size
    local size=$(du -sh "$backup_subdir" | cut -f1)
    log_success "Backup complete: $backup_name ($size)"
    log_info "Contents: .env, $COMPOSE_FILE, config/, packages/"
}

# Restore from a backup
restore_deployment() {
    local backup_name="$1"
    local backup_path="$BACKUP_DIR/$backup_name"

    # Validate backup folder exists
    if [[ ! -d "$backup_path" ]]; then
        log_error "Backup folder not found: $backup_path"
        log_info "Available backups:"
        ls -d "$BACKUP_DIR"/*/ 2>/dev/null | xargs -n1 basename || echo "  (none)"
        return 1
    fi

    # Validate backup contents
    local missing_files=()
    [[ ! -f "$backup_path/$COMPOSE_FILE" ]] && missing_files+=("$COMPOSE_FILE")
    [[ ! -d "$backup_path/config" ]] && missing_files+=("config/")

    if [[ ${#missing_files[@]} -gt 0 ]]; then
        log_error "Backup is incomplete. Missing: ${missing_files[*]}"
        return 1
    fi

    log_info "Restoring from backup: $backup_name"

    # Get version from backup .env
    local restore_version=$(grep -E "^VERSION=" "$backup_path/.env" 2>/dev/null | cut -d= -f2 | tr -d '\r')
    if [[ -n "$restore_version" ]]; then
        log_info "Backup version: $restore_version"
    fi

    # Stop running services
    log_info "Stopping services..."
    docker compose -f "$COMPOSE_FILE" down 2>/dev/null || true

    # Restore config files. Replace the directories rather than copying into them
    # (cp -r into an existing config/ would create config/config).
    log_info "Restoring configuration files..."
    [[ -f "$backup_path/.env" ]] && cp "$backup_path/.env" .env
    cp "$backup_path/$COMPOSE_FILE" "$COMPOSE_FILE"
    rm -rf config
    cp -a "$backup_path/config" config
    log_success "Configuration restored"

    if [[ -d "$backup_path/packages" ]]; then
        rm -rf packages
        cp -a "$backup_path/packages" packages
        log_success "Package storage restored"
    else
        log_warn "Backup has no packages/ (made by an older deploy.sh); package storage left as is"
    fi

    # Fix line endings if needed
    fix_env_line_endings

    # Start all services
    log_info "Starting services..."
    docker compose -f "$COMPOSE_FILE" up -d --remove-orphans

    log_info "Waiting for health check..."
    if wait_for_healthy; then
        log_success "All services running"
    else
        log_warn "Check logs with:"
        log_info "  docker compose -f $COMPOSE_FILE logs"
    fi

    log_success "Restore complete!"
    echo ""
    docker compose -f "$COMPOSE_FILE" ps
}

# Parse arguments
DEPLOY_VERSION=""
LOAD_IMAGE=false
SKIP_BACKUP=false
DRY_RUN=false
RESTORE_BACKUP=""
PULL_IMAGE=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --pull)
            PULL_IMAGE=true
            shift
            ;;
        --load)
            LOAD_IMAGE=true
            shift
            ;;
        --no-load)
            LOAD_IMAGE=false
            shift
            ;;
        --no-backup)
            SKIP_BACKUP=true
            shift
            ;;
        --restore)
            if [[ -z "$2" || "$2" == --* ]]; then
                log_error "--restore requires a backup folder name"
                usage
            fi
            RESTORE_BACKUP="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        -h|--help)
            usage
            ;;
        *)
            if [[ -z "$DEPLOY_VERSION" ]]; then
                DEPLOY_VERSION="$1"
            else
                log_error "Unknown argument: $1"
                usage
            fi
            shift
            ;;
    esac
done

# Handle restore mode
if [[ -n "$RESTORE_BACKUP" ]]; then
    cd "$DEPLOY_DIR"
    if restore_deployment "$RESTORE_BACKUP"; then
        exit 0
    else
        exit 1
    fi
fi

if [[ -z "$DEPLOY_VERSION" ]]; then
    log_error "Version is required"
    usage
fi

# Validate deployment directory
if [[ ! -d "$DEPLOY_DIR" ]]; then
    log_error "Deployment directory not found: $DEPLOY_DIR"
    log_info "Create it with: sudo mkdir -p $DEPLOY_DIR && sudo chown \$USER:\$USER $DEPLOY_DIR"
    exit 1
fi

cd "$DEPLOY_DIR"

# Check for required files
if [[ ! -f "$COMPOSE_FILE" ]]; then
    log_error "Compose file not found: $DEPLOY_DIR/$COMPOSE_FILE"
    exit 1
fi

# The same image name the compose file uses: INSTELLA_IMAGE from .env, or the locally built one.
IMAGE_NAME=$(get_env_value "INSTELLA_IMAGE")
IMAGE_NAME="${IMAGE_NAME:-instella-server}"
IMAGE_TAG="${IMAGE_NAME}:${DEPLOY_VERSION}"
if [[ "$PULL_IMAGE" == true && "$LOAD_IMAGE" == true ]]; then
    log_error "--pull and --load are mutually exclusive"
    exit 1
fi
TAR_FILE="${IMAGE_NAME}-${DEPLOY_VERSION}.tar"

log_info "Deploying Instella Server v${DEPLOY_VERSION}"
log_info "Deploy directory: $DEPLOY_DIR"

if [[ "$DRY_RUN" == true ]]; then
    log_warn "DRY RUN - no changes will be made"
fi

# Load image from tar if requested
if [[ "$LOAD_IMAGE" == true ]]; then
    if [[ ! -f "$TAR_FILE" ]]; then
        log_error "Image archive not found: $DEPLOY_DIR/$TAR_FILE"
        exit 1
    fi

    log_info "Loading Docker image from $TAR_FILE..."
    if [[ "$DRY_RUN" == false ]]; then
        docker load -i "$TAR_FILE"
        log_success "Image loaded successfully"
    fi
fi

# Pull the image if requested. Pulling does not touch the running container: it is replaced
# only after the backup, by "docker compose up".
if [[ "$PULL_IMAGE" == true ]]; then
    log_info "Pulling $IMAGE_TAG..."
    if [[ "$DRY_RUN" == false ]]; then
        docker pull "$IMAGE_TAG"
        log_success "Image pulled"
    fi
fi

# Verify image exists
if [[ "$DRY_RUN" == false ]]; then
    if ! docker image inspect "$IMAGE_TAG" &> /dev/null; then
        log_error "Image not found: $IMAGE_TAG"
        log_info "Available instella-server images:"
        docker images "$IMAGE_NAME" --format "  {{.Tag}}"
        exit 1
    fi
fi

# Record current version for potential rollback
CURRENT_VERSION=$(get_env_value "VERSION")
CURRENT_VERSION="${CURRENT_VERSION:-unknown}"
if [[ "$CURRENT_VERSION" != "$DEPLOY_VERSION" ]]; then
    log_info "Current version: $CURRENT_VERSION -> New version: $DEPLOY_VERSION"
fi

# Backup before deployment
if [[ "$SKIP_BACKUP" == false && "$CURRENT_VERSION" != "unknown" ]]; then
    if [[ "$DRY_RUN" == false ]]; then
        if ! backup_deployment "$CURRENT_VERSION"; then
            docker compose -f "$COMPOSE_FILE" start 2>/dev/null || true   # the backup stopped it
            log_error "Backup failed. Use --no-backup to skip backup and continue anyway."
            exit 1
        fi
    else
        log_info "Would backup to: $BACKUP_DIR/instella-${CURRENT_VERSION}-<timestamp>/"
    fi
elif [[ "$SKIP_BACKUP" == true ]]; then
    log_warn "Skipping backup (--no-backup specified)"
fi

# Update .env with new version
log_info "Updating VERSION in .env..."
if [[ "$DRY_RUN" == false ]]; then
    # Create .env if it doesn't exist
    if [[ ! -f ".env" ]]; then
        echo "VERSION=${DEPLOY_VERSION}" > .env
        echo "INSTELLA_PORT=8580" >> .env
        log_success "Created .env file"
    else
        # Update existing .env
        TEMP_FILE=$(mktemp)
        VERSION_FOUND=false

        while IFS= read -r line || [[ -n "$line" ]]; do
            if [[ "$line" =~ ^VERSION= ]]; then
                echo "VERSION=${DEPLOY_VERSION}" >> "$TEMP_FILE"
                VERSION_FOUND=true
            else
                echo "$line" >> "$TEMP_FILE"
            fi
        done < .env

        # Append VERSION if it wasn't found
        if [[ "$VERSION_FOUND" == false ]]; then
            echo "VERSION=${DEPLOY_VERSION}" >> "$TEMP_FILE"
        fi

        mv "$TEMP_FILE" .env
    fi

    # Verify the update
    UPDATED_VERSION=$(get_env_value "VERSION")
    if [[ "$UPDATED_VERSION" == "$DEPLOY_VERSION" ]]; then
        log_success "VERSION updated to ${DEPLOY_VERSION}"
    else
        log_error "Failed to update VERSION in .env"
        exit 1
    fi
fi

# Create the volume directories, owned by the container's user
prepare_volumes

# Deploy with docker compose
log_info "Starting deployment..."
if [[ "$DRY_RUN" == false ]]; then
    docker compose -f "$COMPOSE_FILE" up -d --remove-orphans

    log_info "Waiting for health check..."
    if ! wait_for_healthy; then
        log_error "Deployment may have issues. Check logs with:"
        log_info "  docker compose -f $COMPOSE_FILE logs instella-server"
        log_info "To go back, restore the backup made before this deployment: $0 --restore <backup-folder>"
        exit 1
    fi

    log_success "Deployment complete!"
    echo ""
    docker compose -f "$COMPOSE_FILE" ps
else
    log_info "Would run: docker compose -f $COMPOSE_FILE up -d --remove-orphans"
fi

echo ""
log_info "Useful commands:"
echo "  View logs:     docker compose -f $COMPOSE_FILE logs -f instella-server"
echo "  Check status:  docker compose -f $COMPOSE_FILE ps"
echo "  Stop:          docker compose -f $COMPOSE_FILE down"
echo "  List backups:  ls -d $BACKUP_DIR/*/"
