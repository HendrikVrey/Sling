using System.IO;
using System.Windows;
using Sling.Core.Rendering;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// Noticing that the open file has changed on disk, and what to do about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sling recommends git and an external editor, so the file in the pane is expected to
/// change without Sling.</b> That is the reason saving here is explicit rather than
/// continuous, and it was the one thing nobody was watching: activation already re-reads the
/// environment files and re-walks the folder, both for exactly this workflow, while the
/// document itself was assumed to be whatever Sling last put there. A pull, a branch switch,
/// a rebase or a colleague's edit in the next window all left the pane holding a version of
/// the file nothing on screen said was old - and the first sign of it was <c>Ctrl+S</c>
/// quietly writing it back over the new one.
/// </para>
/// <para>
/// <b>Two checks, cheap first.</b> A stat answers "may this still be trusted" on every
/// activation for almost nothing; only when the length or the write time has moved is the
/// file read and fingerprinted. That second half is not an optimisation, it is what stops
/// the notice being wrong: a checkout touches the write time of every file it visits,
/// whether or not it changed one, so a stat alone would announce a change on files that had
/// none - and a notice that cries wolf is a notice people learn to dismiss.
/// </para>
/// <para>
/// <b>Nothing here decides anything.</b> Reloading discards work and keeping discards
/// somebody else's; both are the user's call, and the third answer - show me what is
/// different - is the one that makes the other two answerable.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The pane header the comparison borrows the response pane under.</summary>
    private const string CompareInspector = "Changed on disk";

    /// <summary>
    /// What the file looked like when Sling last read or wrote it, and null when there is no
    /// file behind the buffer.
    /// </summary>
    private DocumentPeek? _diskPeek;

    /// <summary>
    /// A fingerprint of the text Sling believes is on disk.
    /// </summary>
    /// <remarks>
    /// Of the text rather than a copy of it: this has to be carried for the life of a
    /// document that may be sixteen megabytes, and the only question asked of it is whether
    /// two versions are the same.
    /// </remarks>
    private string _diskHash = string.Empty;

    /// <summary>True while a check is in flight, so activations do not stack them up.</summary>
    private bool _checkingDisk;

    /// <summary>Which way the file moved, or null when the notice is down.</summary>
    private DiskChange? _diskChange;

    /// <summary>
    /// True once the user has been told the file is gone and has said they know.
    /// </summary>
    /// <remarks>
    /// A missing file has no baseline to move, so "Keep mine" has nothing to adopt and the
    /// notice would come back on every activation - which is a notice nobody can dismiss, and
    /// therefore one everybody learns to ignore. Cleared whenever the document is read or
    /// written, and whenever the file turns out to be there after all.
    /// </remarks>
    private bool _goneAcknowledged;

    /// <summary>What happened to the file behind the buffer.</summary>
    private enum DiskChange
    {
        /// <summary>It is still there and its contents are not what Sling last saw.</summary>
        Changed,

        /// <summary>It is not there any more.</summary>
        Gone,
    }

    /// <summary>
    /// Records what is on disk as the version the buffer agrees with.
    /// </summary>
    /// <remarks>
    /// Called from the two places the buffer and the file are known to match: straight after
    /// a read, and straight after a write. Taking the stat after the text rather than before
    /// it is deliberate - a write that lands between the two would be caught by the next
    /// check rather than adopted silently as the baseline.
    /// </remarks>
    private void RecordDiskState(string? path, string text)
    {
        HideDiskNotice();

        _goneAcknowledged = false;

        if (path is null)
        {
            _diskPeek = null;
            _diskHash = string.Empty;
            return;
        }

        _diskHash = RequestFileStore.HashOf(text);

        // The one stat in this file that runs on the dispatcher, and the exception is
        // argued rather than overlooked: both callers reach here immediately after reading
        // or writing this very file, so the handle is warm and the volume has just proved it
        // answers. The activation check, which runs against a share that may have gone away
        // since, does its stat off the thread.
        _diskPeek = RequestFileStore.Peek(path);
    }

    /// <summary>
    /// Asks whether the open file still matches the buffer, and says so if it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On activation, beside the environment reload and the folder re-walk, for the reason
    /// all three share: the workflow being served is "changed it over there, came back", and
    /// coming back is the event. A <see cref="FileSystemWatcher"/> would report a branch
    /// switch thousands of times and would still have to be answered on the dispatcher.
    /// </para>
    /// <para>
    /// Everything that touches the file system runs off the dispatcher. A stat is fast on a
    /// local disk and takes the whole of a network timeout on a share that has gone away, and
    /// this runs every time the window comes forward.
    /// </para>
    /// </remarks>
    private void CheckDocumentOnDisk()
    {
        if (_closed || _checkingDisk || _documentPath is not { } path)
        {
            return;
        }

        _checkingDisk = true;

        RunGuarded(async () =>
        {
            try
            {
                var peek = await Task.Run(() => RequestFileStore.Peek(path)).ConfigureAwait(true);

                // The document can have been closed or replaced while the stat was running,
                // in which case this answer is about a file nobody is looking at.
                if (_closed || !IsOpenDocument(path))
                {
                    return;
                }

                if (peek is null)
                {
                    if (!_goneAcknowledged)
                    {
                        ShowDiskNotice(DiskChange.Gone);
                    }

                    return;
                }

                // It is there, so anything said about it being gone is history.
                _goneAcknowledged = false;

                if (_diskPeek is { } known && peek.Value == known)
                {
                    HideDiskNotice();
                    return;
                }

                string text;

                try
                {
                    text = await RequestFileStore.ReadAsync(path, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Caught mid-write by whatever is editing it, or locked by a scanner.
                    // Saying nothing is right: the next activation asks again, and a notice
                    // raised on a transient lock is a notice about the wrong thing.
                    return;
                }

                if (_closed || !IsOpenDocument(path))
                {
                    return;
                }

                // The stat moved and the content did not. A checkout of a branch carrying
                // the same file writes it again, so this is the common case rather than a
                // curiosity - adopting the new stat here is what stops the notice appearing
                // every time somebody switches branches.
                if (string.Equals(RequestFileStore.HashOf(text), _diskHash, StringComparison.Ordinal))
                {
                    _diskPeek = peek;
                    HideDiskNotice();
                    return;
                }

                ShowDiskNotice(DiskChange.Changed);
            }
            finally
            {
                _checkingDisk = false;
            }
        });
    }

    /// <summary>
    /// Whether writing over <paramref name="path"/> would destroy a version of it that
    /// nobody has been shown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The strip was an announcement and not a guard, and that is the difference this
    /// closes.</b> Telling somebody the file changed and then writing over it anyway on the
    /// next <c>Ctrl+S</c> is the shape this codebase has already met twice under another
    /// name: a warning is not a mitigation. Worse, it was reachable through the notice's own
    /// Reload button - which asks about unsaved work, whose default answer is Save, whose
    /// effect is to overwrite the version being reloaded.
    /// </para>
    /// <para>
    /// <b>A moved timestamp is not a refusal.</b> The content is compared, exactly as the
    /// activation check does, because a checkout rewrites every file it visits and refusing
    /// to save after every branch switch would be the notice crying wolf in the one place it
    /// costs work.
    /// </para>
    /// <para>
    /// <b>Only the file this document came from.</b> Save As to another path has no baseline
    /// to compare against, and the dialog has already asked about overwriting whatever is
    /// there. A file that has been deleted is written again rather than refused - that is
    /// what the strip's own "Save it again" means.
    /// </para>
    /// <para>
    /// "Keep mine" stays the way to overrule this, because it moves the baseline first. That
    /// is the point of having it: the refusal names it.
    /// </para>
    /// </remarks>
    private async Task<bool> MayOverwriteAsync(string path)
    {
        if (_documentPath is not { } current
            || !PathsMatch(current, path)
            || _diskPeek is not { } baseline)
        {
            return true;
        }

        var now = await Task.Run(() => RequestFileStore.Peek(path)).ConfigureAwait(true);

        if (now is null || now.Value == baseline)
        {
            return true;
        }

        var hash = await Task.Run(() => RequestFileStore.HashOnDisk(path)).ConfigureAwait(true);

        if (_closed || !IsOpenDocument(path))
        {
            // The document was replaced while this was being answered, so the write it was
            // asked about is no longer the one about to happen.
            return false;
        }

        if (string.Equals(hash, _diskHash, StringComparison.Ordinal))
        {
            // Only the timestamp moved. Adopted here so the next save does not pay for the
            // same read again.
            _diskPeek = now;
            return true;
        }

        ShowDiskNotice(DiskChange.Changed);

        StatusLeft.Text = $"Not saved: '{Path.GetFileName(path)}' has changed on disk. "
            + "Compare it, or choose Keep mine and save again.";

        return false;
    }

    private static bool PathsMatch(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private void ShowDiskNotice(DiskChange change)
    {
        _diskChange = change;

        var name = DocumentName;

        DiskNoticeText.Text = change == DiskChange.Gone
            ? $"'{name}' is no longer on disk. What is in the pane is the only copy."
            : $"'{name}' has changed on disk since it was opened here.";

        // The Reload / Compare pair means nothing about a file that is gone, and offering
        // them would be offering to read something that is not there. Save it again is the
        // only useful act in that state, and it is the wrong one to offer in the other.
        //
        // Keep mine survives both, because both need a way out: a notice that cannot be
        // dismissed is a notice everybody learns to ignore.
        var gone = change == DiskChange.Gone;

        DiskReloadButton.Visibility = gone ? Visibility.Collapsed : Visibility.Visible;
        DiskCompareButton.Visibility = gone ? Visibility.Collapsed : Visibility.Visible;
        DiskSaveButton.Visibility = gone ? Visibility.Visible : Visibility.Collapsed;

        DiskKeepButton.ToolTip = gone
            ? "Leave the pane alone and stop saying the file is missing"
            : "Leave the pane alone and stop asking about this change";

        DiskNotice.Visibility = Visibility.Visible;
    }

    private void HideDiskNotice()
    {
        _diskChange = null;

        if (!_closed)
        {
            DiskNotice.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Throws the buffer away and reads the file again.</summary>
    /// <remarks>
    /// <b>Keeping the work means saving it <em>elsewhere</em>, and that is not a nicety.</b>
    /// The ordinary unsaved-work question offers Save, which writes over this document's own
    /// file - which is the version being reloaded. Answering the default would therefore
    /// destroy exactly what the click asked for. <see cref="MayOverwriteAsync"/> now refuses
    /// that write, and this sends the answer somewhere it can succeed.
    /// </remarks>
    private void OnReloadFromDisk(object sender, RoutedEventArgs e) =>
        RunGuarded(async () =>
        {
            if (_documentPath is not { } path)
            {
                return;
            }

            var confirmed = await ConfirmDiscardAsync(
                "before replacing them with the version on disk",
                saveElsewhere: true).ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }

            await LoadDocumentAsync(path).ConfigureAwait(true);
        });

    /// <summary>Writes the buffer back over a path whose file has gone.</summary>
    private void OnSaveOverDisk(object sender, RoutedEventArgs e) => RunGuarded(() => SaveAsync());

    /// <summary>
    /// Leaves the buffer alone and stops asking about this change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The version on disk becomes the baseline even though the buffer disagrees with it,
    /// which is what "stop asking" has to mean: keeping the old baseline would raise the same
    /// notice on the next activation, and a notice that cannot be dismissed is a notice that
    /// gets dismissed without being read. A <em>further</em> change on disk still raises it,
    /// because the baseline moved rather than the check being switched off.
    /// </para>
    /// <para>
    /// <b>And the buffer is marked dirty, which is not a technicality.</b> Somebody who
    /// chooses to keep what is in front of them is saying it is the version that should
    /// survive - and if they had not typed in it, it was clean, so Save would have been
    /// greyed out and there would have been no way to act on the choice they had just made.
    /// </para>
    /// </remarks>
    private void OnKeepMineOverDisk(object sender, RoutedEventArgs e) =>
        RunGuarded(async () =>
        {
            var change = _diskChange;
            var path = _documentPath;

            HideDiskNotice();

            if (!_dirty)
            {
                _dirty = true;
                UpdateTitle();
            }

            if (change == DiskChange.Gone)
            {
                // Nothing to adopt: there is no file to take a baseline from, and the check
                // has no way to tell "still gone" from "gone again". Remembered as answered
                // instead, until the document is loaded or written again.
                _goneAcknowledged = true;
                StatusLeft.Text = "Keeping what is in the pane. Ctrl+S writes it back to that path.";
                return;
            }

            StatusLeft.Text = "Keeping what is in the pane. Ctrl+S writes it over the version on disk.";

            if (path is null)
            {
                return;
            }

            // Off the dispatcher, because this reads the whole file - and in the order
            // RecordDiskState uses, for the reason it gives: a write landing between the two
            // must be caught by the next check rather than adopted silently as the baseline.
            var hash = await Task.Run(() => RequestFileStore.HashOnDisk(path)).ConfigureAwait(true);
            var peek = await Task.Run(() => RequestFileStore.Peek(path)).ConfigureAwait(true);

            if (_closed || !IsOpenDocument(path))
            {
                return;
            }

            _diskHash = hash;
            _diskPeek = peek;
        });

    /// <summary>Puts the difference between the buffer and the file in the response pane.</summary>
    private void OnCompareWithDisk(object sender, RoutedEventArgs e) =>
        RunGuarded(async () =>
        {
            if (_documentPath is not { } path)
            {
                return;
            }

            if (IsInspecting(CompareInspector))
            {
                ReturnToResponse();
                return;
            }

            string theirs;

            try
            {
                theirs = await RequestFileStore.ReadAsync(path, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusLeft.Text = $"Could not read '{Path.GetFileName(path)}': {ex.Message}";
                return;
            }

            if (_closed || !IsOpenDocument(path))
            {
                return;
            }

            var mine = RequestPane.Text;

            // Off the dispatcher: the comparison is bounded but not bounded small, and the
            // one document it is slowest on is the one somebody is most anxious about.
            var diff = await Task
                .Run(() => TextDiff.Unified(mine, theirs, "what is in the pane", "what is on disk"))
                .ConfigureAwait(true);

            if (_closed || !IsOpenDocument(path))
            {
                return;
            }

            ShowInspector(CompareInspector, diff);

            StatusLeft.Text = $"Comparing the pane against {path}.";
            StatusRight.Text = string.Empty;
        });
}
