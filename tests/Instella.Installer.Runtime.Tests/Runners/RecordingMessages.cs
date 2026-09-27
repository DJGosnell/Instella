using System.Collections.Generic;
using Instella.Installer.Runtime.Runners;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>Records what the installer would have shown; <see cref="Confirm"/> answers <see cref="Answer"/>.</summary>
internal sealed class RecordingMessages : IUserMessages
{
    public List<(string Title, string Text)> Errors { get; } = [];
    public List<(string Title, string Text)> Notices { get; } = [];
    public List<(string Title, string Text)> Questions { get; } = [];
    public bool Answer { get; init; } = true;

    public void Error(string title, string text) => Errors.Add((title, text));
    public void Info(string title, string text) => Notices.Add((title, text));

    public bool Confirm(string title, string text)
    {
        Questions.Add((title, text));
        return Answer;
    }
}
