namespace Zdev.Common;

/// <summary>Which language the shell in the pane speaks, for the one caller that has to write a
/// command into it rather than a keystroke.</summary>
public enum ShellFamily
{
    Posix,
    PowerShell,
    CommandProcessor,
}
