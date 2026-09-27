using Instella.Server.Models;

namespace Instella.Server.Data.Entities;

/// <summary>
/// A durable upload session, stored so a server restart neither loses sessions nor leaks
/// reference counts. Blobs uploaded into a session are counted only when it completes.
/// </summary>
public class UploadSessionRecord
{
    public Guid Id { get; set; }

    /// <summary>The API key that started the session; every later session call must use it.</summary>
    public long? ApiKeyId { get; set; }

    public long PackageDbId { get; set; }

    public required string PackageId { get; set; }

    public required string Version { get; set; }

    public required string Channel { get; set; }

    public TargetOS OS { get; set; }

    public Architecture Architecture { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public ICollection<UploadSessionFile> Files { get; set; } = [];

    public ICollection<UploadSessionInstaller> Installers { get; set; } = [];
}

/// <summary>One file uploaded into a session; re-uploading a path replaces its row.</summary>
public class UploadSessionFile
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public UploadSessionRecord Session { get; set; } = null!;

    public required string RelativePath { get; set; }

    public required string ContentHash { get; set; }

    public long Size { get; set; }

    /// <summary>The content already existed when this file was uploaded.</summary>
    public bool Deduplicated { get; set; }
}

/// <summary>An installer uploaded into a session; re-uploading a kind replaces its row.</summary>
public class UploadSessionInstaller
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public UploadSessionRecord Session { get; set; } = null!;

    /// <summary><c>online</c> or <c>offline</c>.</summary>
    public required string Kind { get; set; }

    public required string FileName { get; set; }

    public required string ContentHash { get; set; }

    public long Size { get; set; }

    /// <summary>The content already existed when this installer was uploaded.</summary>
    public bool Deduplicated { get; set; }
}
