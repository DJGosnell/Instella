using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.UI.Windows;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;
}

/// <summary>
/// Win32 ACCEL table entry. <b>Packed to 1-byte alignment</b> to match the
/// Windows SDK layout (<c>#pragma pack(1)</c>) — with default C# packing the
/// struct would grow to 8 bytes and CreateAcceleratorTableW would silently
/// read the wrong fields.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ACCEL
{
    public byte fVirt;
    public ushort key;
    public ushort cmd;
}

/// <summary><c>ACCEL.fVirt</c> flag bits.</summary>
internal static class FVIRT
{
    public const byte VIRTKEY = 0x01;
    public const byte NOINVERT = 0x02;
    public const byte SHIFT = 0x04;
    public const byte CONTROL = 0x08;
    public const byte ALT = 0x10;
}

/// <summary>Virtual-key codes used by the widget host's accelerator table.</summary>
internal static class VK
{
    public const ushort B = 0x42;
    public const ushort C = 0x43;
    public const ushort F = 0x46;
    public const ushort N = 0x4E;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INITCOMMONCONTROLSEX
{
    public uint dwSize;
    public uint dwICC;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct LOGFONTW
{
    public int lfHeight;
    public int lfWidth;
    public int lfEscapement;
    public int lfOrientation;
    public int lfWeight;
    public byte lfItalic;
    public byte lfUnderline;
    public byte lfStrikeOut;
    public byte lfCharSet;
    public byte lfOutPrecision;
    public byte lfClipPrecision;
    public byte lfQuality;
    public byte lfPitchAndFamily;
    public fixed char lfFaceName[32];
}

internal static class ControlIds
{
    // Welcome page
    internal const int LblWelcomeHeader = 100;
    internal const int LblWelcomeSubtext = 101;
    internal const int LblWelcomeVersion = 102;

    // Options page
    internal const int LblOptionsHeader = 200;
    internal const int LblInstallPath = 201;
    internal const int TxtInstallPath = 202;
    internal const int BtnBrowse = 203;
    internal const int ChkDesktopShortcut = 204;
    internal const int ChkStartMenuShortcut = 205;
    internal const int ChkAddToPath = 206;

    // Progress page
    internal const int LblProgressHeader = 300;
    internal const int LblProgressStatus = 301;
    internal const int PrgProgress = 302;

    // Complete page
    internal const int LblCompleteHeader = 400;
    internal const int LblCompleteSubtext = 401;
    internal const int ChkLaunchAfterInstall = 402;

    // Error page
    internal const int LblErrorHeader = 500;
    internal const int LblErrorMessage = 501;

    // Footer buttons
    internal const int BtnCancel = 900;
    internal const int BtnBack = 901;
    internal const int BtnNext = 902;
    internal const int BtnInstall = 903;
    internal const int BtnFinish = 904;
}

internal static class WM
{
    internal const uint CREATE = 0x0001;
    internal const uint DESTROY = 0x0002;
    internal const uint CLOSE = 0x0010;
    internal const uint SETFONT = 0x0030;
    internal const uint SETTEXT = 0x000C;
    internal const uint GETTEXT = 0x000D;
    internal const uint SIZE = 0x0005;
    internal const uint COMMAND = 0x0111;
    internal const uint CTLCOLORSTATIC = 0x0138;
    internal const uint APP = 0x8000;
    internal const uint STATE_CHANGED = APP + 1;
}

internal static class WS
{
    internal const uint OVERLAPPED = 0x00000000;
    internal const uint POPUP = 0x80000000;
    internal const uint CAPTION = 0x00C00000;
    internal const uint SYSMENU = 0x00080000;
    internal const uint MINIMIZEBOX = 0x00020000;
    internal const uint VISIBLE = 0x10000000;
    internal const uint CHILD = 0x40000000;
    internal const uint CLIPCHILDREN = 0x02000000;
    internal const uint CLIPSIBLINGS = 0x04000000;
    internal const uint TABSTOP = 0x00010000;
    internal const uint GROUP = 0x00020000;
    internal const uint BORDER = 0x00800000;
    internal const uint VSCROLL = 0x00200000;
    internal const uint HSCROLL = 0x00100000;
    internal const uint DISABLED = 0x08000000;

    // Edit-control styles (bit-flag subset — combine with WS.CHILD/VISIBLE/BORDER)
    internal const uint ES_AUTOHSCROLL = 0x0080;
    internal const uint ES_AUTOVSCROLL = 0x0040;
    internal const uint ES_MULTILINE = 0x0004;
    internal const uint ES_READONLY = 0x0800;
    internal const uint ES_WANTRETURN = 0x1000;

    // Button styles
    internal const uint BS_PUSHBUTTON = 0x00000000;
    internal const uint BS_DEFPUSHBUTTON = 0x00000001;
    internal const uint BS_AUTOCHECKBOX = 0x00000003;
    internal const uint BS_AUTORADIOBUTTON = 0x00000009;
    internal const uint BS_GROUPBOX = 0x00000007;

    // Static styles
    internal const uint SS_LEFT = 0x00000000;
    internal const uint SS_BITMAP = 0x0000000E;
    internal const uint SS_CENTERIMAGE = 0x00000200;
    internal const uint SS_NOTIFY = 0x00000100;

    // ComboBox styles
    internal const uint CBS_DROPDOWNLIST = 0x0003;
    internal const uint CBS_HASSTRINGS = 0x0200;

    internal const uint INSTALLER_WINDOW = OVERLAPPED | CAPTION | SYSMENU | MINIMIZEBOX;
}

// Edit-control notifications (high word of WM_COMMAND.wParam).
internal static class EN
{
    internal const int CHANGE = 0x0300;
    internal const int UPDATE = 0x0400;
}

// ComboBox notifications (high word of WM_COMMAND.wParam).
internal static class CBN
{
    internal const int SELCHANGE = 1;
    internal const int EDITCHANGE = 5;
}

// ComboBox messages.
internal static class CB
{
    internal const uint ADDSTRING = 0x0143;
    internal const uint DELETESTRING = 0x0144;
    internal const uint GETCURSEL = 0x0147;
    internal const uint SETCURSEL = 0x014E;
    internal const uint GETLBTEXT = 0x0148;
    internal const uint GETLBTEXTLEN = 0x0149;
    internal const uint RESETCONTENT = 0x014B;
}

// SetWindowPos flags. NOZORDER | NOACTIVATE for move/size without stealing focus.
internal static class SWP
{
    internal const uint NOSIZE = 0x0001;
    internal const uint NOMOVE = 0x0002;
    internal const uint NOZORDER = 0x0004;
    internal const uint NOREDRAW = 0x0008;
    internal const uint NOACTIVATE = 0x0010;
    internal const uint SHOWWINDOW = 0x0040;
    internal const uint HIDEWINDOW = 0x0080;
}

// BM_CLICK programmatically fires a button click (BN_CLICKED notification).
internal static class BM_MSG
{
    internal const uint CLICK = 0x00F5;
}

// DPI awareness context handles. Negative magic values — use as nint casts.
internal static class DPI_AWARENESS_CONTEXT
{
    internal static readonly nint UNAWARE = -1;
    internal static readonly nint SYSTEM_AWARE = -2;
    internal static readonly nint PER_MONITOR_AWARE = -3;
    internal static readonly nint PER_MONITOR_AWARE_V2 = -4;
    internal static readonly nint UNAWARE_GDISCALED = -5;
}

// PROCESS_DPI_AWARENESS values for SetProcessDpiAwareness (shcore.dll).
internal static class PROCESS_DPI
{
    internal const int UNAWARE = 0;
    internal const int SYSTEM_AWARE = 1;
    internal const int PER_MONITOR_AWARE = 2;
}

internal static class SW
{
    internal const int HIDE = 0;
    internal const int SHOW = 5;
}

internal static class SM
{
    internal const int CXSCREEN = 0;
    internal const int CYSCREEN = 1;
    internal const int CXICON = 11;
    internal const int CYICON = 12;
    internal const int CXSMICON = 49;
    internal const int CYSMICON = 50;
}

// LoadImage / LoadIcon constants
internal static class IMAGE
{
    internal const uint ICON = 1;
}

internal static class LR
{
    internal const uint DEFAULTCOLOR = 0x0000;
    internal const uint SHARED = 0x8000; // cache in system icon table; no DestroyIcon needed
    internal const uint DEFAULTSIZE = 0x0040;
}

// Extended window styles. WS_EX_CONTROLPARENT lets IsDialogMessage recurse
// into child-of-child controls so Tab navigation works when controls are
// nested (e.g., group box → button).
internal static class WS_EX
{
    internal const uint CONTROLPARENT = 0x00010000;
}

internal static class PBM
{
    internal const uint SETRANGE32 = 0x0406;
    internal const uint SETPOS = 0x0402;
    internal const uint SETMARQUEE = 0x040A;
}

internal static class PBS
{
    internal const uint SMOOTH = 0x01;
    internal const uint MARQUEE = 0x08;
}

internal static class ICC
{
    internal const uint PROGRESS_CLASS = 0x00000020;
}

internal static class BN
{
    internal const int CLICKED = 0;
}

internal static class BST
{
    internal const int UNCHECKED = 0;
    internal const int CHECKED = 1;
}

internal static class BM
{
    internal const uint GETCHECK = 0x00F0;
    internal const uint SETCHECK = 0x00F1;
}

internal static class IDC
{
    internal const int ARROW = 32512;
}

// MessageBox type flags (subset)
internal static class MB
{
    internal const uint OK = 0x00000000;
    internal const uint YESNOCANCEL = 0x00000003;
    internal const uint YESNO = 0x00000004;
    internal const uint CANCELTRYCONTINUE = 0x00000006;
    internal const uint ICONWARNING = 0x00000030;
    internal const uint ICONINFORMATION = 0x00000040;
    internal const uint ICONERROR = 0x00000010;
    internal const uint ICONQUESTION = 0x00000020;
    internal const uint DEFBUTTON2 = 0x00000100;
    internal const uint TOPMOST = 0x00040000;
}

internal static class IDRESULT
{
    internal const int OK = 1;
    internal const int CANCEL = 2;
    internal const int TRYAGAIN = 10;
    internal const int CONTINUE = 11;
    internal const int YES = 6;
    internal const int NO = 7;
}

internal static class COLOR
{
    internal const int BTNFACE = 15;
}

internal static class FW
{
    internal const int NORMAL = 400;
    internal const int SEMIBOLD = 600;
}

// DWM window attributes (see dwmapi.h). Many require Win10 20H1+ or Win11.
// Passing an unsupported attribute returns E_INVALIDARG, which we ignore.
internal static class DWMWA
{
    internal const uint USE_IMMERSIVE_DARK_MODE = 20;   // Win10 20H1+
    internal const uint WINDOW_CORNER_PREFERENCE = 33;  // Win11
}

// Values for DWMWA_WINDOW_CORNER_PREFERENCE
internal static class DWMWCP
{
    internal const int DEFAULT = 0;
    internal const int DONOTROUND = 1;
    internal const int ROUND = 2;
    internal const int ROUNDSMALL = 3;
}

internal static class FOS
{
    internal const uint PICKFOLDERS = 0x00000020;
}

internal static class COINIT
{
    internal const uint APARTMENTTHREADED = 0x2;
}

internal static class CLSCTX
{
    internal const uint INPROC_SERVER = 1;
}

internal static class SIGDN
{
    internal const uint FILESYSPATH = 0x80058000;
}
