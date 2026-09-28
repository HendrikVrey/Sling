using Sling.Core.Navigation;

namespace Sling.Core.Tests;

/// <summary>
/// Which Quick Open rows a query finds, and in what order.
/// </summary>
/// <remarks>
/// The ranking is the whole feature: a palette that finds the right row and puts it eleventh
/// is a palette people go back to scrolling the tree instead of using.
/// </remarks>
public sealed class QuickOpenSearchTests
{
    private static readonly QuickOpenEntry OrdersFile = new(
        QuickOpenKind.Document,
        @"C:\work\api\orders\orders.http",
        "orders.http",
        "orders",
        Name: "",
        Method: "",
        Target: "",
        Line: 0);

    private static readonly QuickOpenEntry CreateOrder = new(
        QuickOpenKind.Request,
        @"C:\work\api\orders\orders.http",
        "orders.http",
        "orders",
        "create an order",
        "POST",
        "{{base}}/orders",
        12);

    private static readonly QuickOpenEntry ListOrders = new(
        QuickOpenKind.Request,
        @"C:\work\api\orders\orders.http",
        "orders.http",
        "orders",
        "list them",
        "GET",
        "{{base}}/orders",
        3);

    private static readonly QuickOpenEntry Login = new(
        QuickOpenKind.Request,
        @"C:\work\api\auth\auth.http",
        "auth.http",
        "auth",
        "login",
        "POST",
        "{{base}}/oauth/token",
        4);

    private static readonly QuickOpenEntry[] All = [OrdersFile, ListOrders, CreateOrder, Login];

    [Fact]
    public void Nothing_typed_answers_everything_in_the_order_it_was_given()
    {
        // An empty popup on the first keypress reads as a feature that has not loaded, and
        // the palette is also a way to see what is there.
        Assert.Equal(All, QuickOpenSearch.Rank(All, string.Empty, 10));
    }

    [Fact]
    public void A_limit_is_honoured()
    {
        Assert.Equal(2, QuickOpenSearch.Rank(All, string.Empty, 2).Count);
        Assert.Equal(2, QuickOpenSearch.Rank(All, "orders", 2).Count);
    }

    [Fact]
    public void A_name_match_outranks_a_url_match_and_a_folder_match()
    {
        // 'orders' is in one row's name, in three rows' collection, and in two rows' URL. A
        // palette that ranked those together would bury the row somebody meant under the
        // rows that merely live near it.
        var found = QuickOpenSearch.Rank(All, "order", 10);

        Assert.Equal(CreateOrder, found[0]);
    }

    [Fact]
    public void Every_word_must_match_and_they_may_match_different_fields()
    {
        // The reason the query splits on whitespace at all: 'post orders' is a verb and a
        // collection, and neither is a syntax anybody has to be told about.
        var found = QuickOpenSearch.Rank(All, "post orders", 10);

        Assert.Equal([CreateOrder], found);
    }

    [Fact]
    public void A_word_that_matches_nothing_removes_the_row()
    {
        Assert.Empty(QuickOpenSearch.Rank(All, "orders unicorn", 10));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal(QuickOpenSearch.Rank(All, "LOGIN", 10), QuickOpenSearch.Rank(All, "login", 10));
    }

    [Fact]
    public void A_match_at_the_start_of_a_field_beats_one_inside_it()
    {
        var early = new QuickOpenEntry(
            QuickOpenKind.Request, @"C:\a.http", "a.http", "", "token exchange", "POST", "{{base}}/t", 1);

        var late = new QuickOpenEntry(
            QuickOpenKind.Request, @"C:\a.http", "a.http", "", "refresh a token", "POST", "{{base}}/r", 5);

        // 'late' is given first, so the given order cannot be what puts 'early' on top.
        var found = QuickOpenSearch.Rank([late, early], "token", 10);

        Assert.Equal(early, found[0]);
    }

    [Fact]
    public void Rows_that_score_the_same_keep_the_order_they_were_given()
    {
        // Stability is what stops the list moving under somebody who is typing: a row whose
        // score has not changed must not change places, or the thing being reached for
        // slides out from under the keystroke that would have selected it.
        var found = QuickOpenSearch.Rank(All, "http", 10);

        Assert.Equal([OrdersFile, ListOrders, CreateOrder, Login], found);
    }

    [Fact]
    public void A_file_is_found_by_its_name_and_by_its_collection()
    {
        Assert.Contains(OrdersFile, QuickOpenSearch.Rank(All, "orders.http", 10));
        Assert.Contains(Login, QuickOpenSearch.Rank(All, "auth", 10));
    }

    [Fact]
    public void A_request_is_found_by_the_url_as_written()
    {
        // As written, braces and all: it is what the document says and therefore what
        // somebody would type. The resolved form is a value that may hold a credential.
        Assert.Equal([Login], QuickOpenSearch.Rank(All, "oauth", 10));
    }
}
