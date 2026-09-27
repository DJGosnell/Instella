namespace Instella.Core.BSDiff;

internal static class Constants
{
    public const int HeaderSize = 32;

    public const int HeaderOffsetSig = 0;
    public const int HeaderOffsetCtrl = sizeof(long) * 1;
    public const int HeaderOffsetDiff = sizeof(long) * 2;
    public const int HeaderOffsetNewData = sizeof(long) * 3;

    /// <summary>
    /// "BSDIFF40". The BSDIFF40 header is exactly this 8-byte signature followed by three
    /// 8-byte offtin-encoded lengths; there is no room for a version byte, so the
    /// signature IS the format version. A future patch format gets a new signature
    /// (for example "INSTDF01"), never a version byte.
    /// </summary>
    public const long Signature = 0x3034464649445342;
}
