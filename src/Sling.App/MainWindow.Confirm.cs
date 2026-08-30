using System.Windows;
using Sling.App.Editor;

namespace Sling.App;

/// <summary>What the user chose when asked about unsaved work.</summary>
internal enum UnsavedChoice
{
    /// <summary>Go back: whatever prompted the question must not happen.</summary>
    Cancel = 0,

    /// <summary>Write the document first, then carry on.</summary>
    Save,

    /// <summary>Carry on and lose the edits.</summary>
    Discard,
}

/// <summary>
/// The unsaved-changes question, as an overlay rather than a <c>MessageBox</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It was the last operating-system dialog in the application, and it looked like one:</b>
/// a white title bar with its own window buttons, a yellow warning triangle, and Yes / No /
/// Cancel, beside a Mica window whose settings panel and name prompt are in-window cards.
/// Hendrik reported it as not fitting the UI, and it did not.
/// </para>
/// <para>
/// <b>Yes / No / Cancel was the worse half.</b> Two of those three words say nothing about
/// what happens, and the reader has to hold "the question was <em>save?</em>, so No means
/// throw the work away" in their head - under time pressure, in front of the one answer here
/// that cannot be undone. <see cref="UnsavedChoice"/> and the buttons name the act instead.
/// </para>
/// <para>
/// <b>Awaiting this is safe on the close path, and that is not obvious.</b>
/// <see cref="OnClosing"/> already cancels the close and re-issues it once the answer is in,
/// precisely because <c>CancelEventArgs.Cancel</c> is read the moment it returns. So there is
/// no caller that needs a synchronous answer, which is what a <c>MessageBox</c> was buying
/// and what would otherwise have made this swap a reintroduction of the close-path hang
/// <c>Sling.md</c> §11 records.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private TaskCompletionSource<UnsavedChoice>? _confirm;

    private bool ConfirmIsOpen => _confirm is not null;

    /// <summary>Asks whether unsaved edits should be written, dropped, or the action called off.</summary>
    /// <param name="consequence">
    /// What is about to happen, in the user's terms: "before closing", "before opening
    /// another file". It is the half of the question a title cannot carry.
    /// </param>
    private Task<UnsavedChoice> AskAboutUnsavedAsync(string consequence)
    {
        // Already up. Answering Cancel rather than opening a second card means the caller
        // that arrived late abandons its action, which is the safe direction: the one already
        // on screen is the one the user is looking at.
        if (ConfirmIsOpen)
        {
            return Task.FromResult(UnsavedChoice.Cancel);
        }

        // Continuations run asynchronously for the reason PromptForNameAsync gives: without
        // it the click that answers this would run the whole rest of the command - a save, a
        // document load, a window close - on the stack of the input event.
        _confirm = new TaskCompletionSource<UnsavedChoice>(TaskCreationOptions.RunContinuationsAsynchronously);

        ConfirmHint.Text = $"{DocumentName} has changes that are not on disk. Save them {consequence}?";

        Overlays.Reveal(ConfirmOverlay, ConfirmCard);

        // Save is focused, not Discard: Enter and Space are the keys people press without
        // reading, and they should land on the answer that keeps the work.
        ConfirmSaveButton.Focus();

        return _confirm.Task;
    }

    private void OnConfirmSave(object sender, RoutedEventArgs e) => CloseConfirm(UnsavedChoice.Save);

    private void OnConfirmDiscard(object sender, RoutedEventArgs e) => CloseConfirm(UnsavedChoice.Discard);

    private void OnConfirmCancel(object sender, RoutedEventArgs e) => CloseConfirm(UnsavedChoice.Cancel);

    private void CloseConfirm(UnsavedChoice choice)
    {
        var pending = _confirm;

        _confirm = null;

        Overlays.Hide(ConfirmOverlay);

        // Answered before focus moves, so the continuation cannot race a control on its way
        // out - and before the focus call below, which the close path never reaches.
        pending?.TrySetResult(choice);

        if (!_closed)
        {
            RequestPane.Focus();
        }
    }
}
