using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;

namespace Sling.App.Collections;

/// <summary>What one row in the collections rail is.</summary>
internal enum CollectionItemKind
{
    /// <summary>A directory. What Sling calls a collection.</summary>
    Folder,

    /// <summary>A <c>.http</c> document.</summary>
    Document,

    /// <summary>One request inside a document. What Postman calls an endpoint.</summary>
    Request,

    /// <summary>
    /// The stand-in under an unexpanded document, so the chevron is there to click.
    /// </summary>
    /// <remarks>
    /// A document's requests are only read when its node is opened — a workspace can hold
    /// hundreds of files and parsing all of them to draw a rail is work nobody asked for.
    /// A <c>TreeViewItem</c> with no children draws no expander at all, so without
    /// a placeholder there is nothing to click and the laziness is invisible in the worst
    /// way: the file looks empty.
    /// </remarks>
    Placeholder,
}

/// <summary>
/// One node of the collections rail, as the <c>TreeView</c> binds to it.
/// </summary>
/// <remarks>
/// <para>
/// A view-model rather than the <see cref="Sling.Persistence.Workspaces.CollectionEntry"/>
/// records directly, for two reasons that both come down to the tree being live: WPF needs
/// <see cref="INotifyPropertyChanged"/> to move a selection or an expander from code, and
/// request rows are filled in after their parent is on screen, which an immutable record
/// cannot express.
/// </para>
/// <para>
/// It owns no logic. Everything that reads a file, parses one or writes one is in
/// <c>MainWindow.Collections.cs</c> and <c>Sling.Persistence</c> — this is the shape the
/// binding needs and nothing else.
/// </para>
/// </remarks>
internal sealed class CollectionItem : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public CollectionItem(CollectionItemKind kind, string label, string? path)
    {
        Kind = kind;
        Label = label;
        Path = path;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CollectionItemKind Kind { get; }

    /// <summary>The text on the row.</summary>
    public string Label { get; }

    /// <summary>
    /// The absolute path of the folder or document this row stands for.
    /// </summary>
    /// <remarks>
    /// A request row carries the path of the document it lives in, so acting on one never
    /// has to walk back up the tree to find out which file it is in.
    /// </remarks>
    public string? Path { get; }

    /// <summary>The verb, on a request row. Empty elsewhere.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>The colour the verb is drawn in, already held to the legibility floor.</summary>
    public Brush MethodBrush { get; init; } = Brushes.Transparent;

    /// <summary>1-based line the request starts on, for a request row.</summary>
    public int Line { get; init; }

    /// <summary>Hover text: the path, or the request target as written.</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// The kind, as three booleans the data triggers can read.
    /// </summary>
    /// <remarks>
    /// A <c>DataTrigger</c> on the enum itself would need <see cref="CollectionItemKind"/>
    /// resolvable from XAML, and it is <c>internal</c> — BAML's type resolution for an
    /// internal type in the same assembly works often enough to look reliable and not
    /// always. Three predicates cost nothing and cannot fail at load time.
    /// </remarks>
    public bool IsFolder => Kind == CollectionItemKind.Folder;

    /// <inheritdoc cref="IsFolder"/>
    public bool IsRequest => Kind == CollectionItemKind.Request;

    /// <inheritdoc cref="IsFolder"/>
    public bool IsPlaceholder => Kind == CollectionItemKind.Placeholder;

    public ObservableCollection<CollectionItem> Children { get; } = [];

    /// <summary>
    /// True once a document's requests have been read, so opening it again is free and
    /// the placeholder is never re-added over real rows.
    /// </summary>
    public bool IsLoaded { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            Raise(nameof(IsExpanded));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
