using System;
using System.IO;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Instella.Installer.Build.Tasks;

/// <summary>
/// MSBuild task that signs one file with <c>InstellaSignCommand</c>. Used for the online
/// installer (<c>InstellaEnabled=false</c>), which has no payload to append and so never runs
/// <see cref="AppendPayloadToSelf"/>, the task that signs the offline installer.
/// </summary>
public sealed class SignFile : MSBuildTask
{
    /// <summary>The file to sign, in place.</summary>
    [Microsoft.Build.Framework.Required]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Command that signs one file, with <c>{0}</c> for its path (<c>InstellaSignCommand</c>),
    /// run through the shell. Empty: nothing is signed.
    /// </summary>
    public string SignCommand { get; set; } = string.Empty;

    /// <summary>Runs the task.</summary>
    public override bool Execute()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Log.LogError($"Instella: the file to sign was not found at '{FilePath}'.");
                return false;
            }
            SignCommandRunner.Run(SignCommand, FilePath, Log);
            return true;
        }
        catch (InstellaBuildException ex)
        {
            Log.LogError(null, ex.Code, null, null, 0, 0, 0, 0, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }
}
