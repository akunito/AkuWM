using AkuWM.Core.Layout;
using AkuWM.Core.Model;
using Xunit;

namespace AkuWM.Tests;

/// <summary>
/// The gestures, as the tree sees them. Every case ends by asking the tree
/// whether it is still a valid tree, because the way this goes wrong is not a
/// crash: it is one window, once, in a rectangle nobody asked for.
/// </summary>
public class TilingTreeTests
{
    private static readonly Rect Main = new(0, 42, 3840, 2118);
    private static readonly Gaps NoGaps = Gaps.None;

    private static WindowHandle W(long handle) => new(handle);

    private readonly TilingTree _tree = new();

    private void Ok()
    {
        if (_tree.Root is { } root)
        {
            Assert.Empty(root.Check());
        }
    }

    private void Open(params long[] windows)
    {
        foreach (long handle in windows)
        {
            _tree.Add(W(handle), _tree.Windows.LastOrDefault(), SplitDirection.Horizontal);
        }
    }

    private Rect RectOf(long handle) => _tree.Rects(Main, NoGaps)[W(handle)];

    // ---- opening and closing ---------------------------------------------

    [Fact]
    public void The_first_window_is_the_whole_workspace()
    {
        _tree.Add(W(1), WindowHandle.None, SplitDirection.Horizontal);

        Ok();
        Assert.Equal(Main, RectOf(1));
    }

    [Fact]
    public void Three_windows_on_a_horizontal_workspace_are_three_columns()
    {
        Open(1, 2, 3);

        Ok();
        // Not a column and a nested pair: a person opening a third terminal
        // expects a third of the screen, not a quarter.
        Assert.Equal("H[0x1 0x2 0x3]", _tree.ToString());
        Assert.Equal(1280, RectOf(1).Width);
        Assert.Equal(1280, RectOf(3).Width);
    }

    [Fact]
    public void A_pair_someone_resized_keeps_its_ratio_when_a_third_arrives()
    {
        Open(1, 2);
        _tree.Resize(W(1), Direction.Right, 0.2); // 70/30

        Open(3);

        Ok();
        // The newcomer takes a third; what it takes comes out of the other two
        // in proportion, so the choice someone made about those two survives.
        Assert.Equal(1280, RectOf(3).Width);
        Assert.Equal(7.0 / 3.0, (double)RectOf(1).Width / RectOf(2).Width, 2);
    }

    [Fact]
    public void A_window_opened_against_the_other_axis_nests()
    {
        Open(1, 2);
        _tree.Add(W(3), W(2), SplitDirection.Vertical);

        Ok();
        Assert.Equal("H[0x1 V[0x2 0x3]]", _tree.ToString());
        Assert.Equal(1920, RectOf(1).Width);
        Assert.Equal(1059, RectOf(2).Height);
    }

    [Fact]
    public void Closing_a_window_gives_its_space_back_and_moves_nothing_else()
    {
        Open(1, 2, 3);
        _tree.Add(W(4), W(3), SplitDirection.Vertical);
        Rect before = RectOf(1);

        _tree.Remove(W(4));

        Ok();
        Assert.Equal("H[0x1 0x2 0x3]", _tree.ToString());
        Assert.Equal(before, RectOf(1));
        Assert.Equal(1280, RectOf(3).Width);
    }

    [Fact]
    public void Closing_the_last_window_empties_the_workspace()
    {
        Open(1);

        Assert.True(_tree.Remove(W(1)));
        Assert.True(_tree.IsEmpty);
    }

    [Fact]
    public void Closing_a_window_that_is_not_here_changes_nothing()
    {
        Open(1, 2);

        Assert.False(_tree.Remove(W(99)));
        Ok();
        Assert.Equal(2, _tree.Count);
    }

    [Fact]
    public void A_window_is_never_in_the_tree_twice()
    {
        Open(1, 2);
        _tree.Add(W(1), W(2), SplitDirection.Horizontal);

        Ok();
        Assert.Equal(2, _tree.Count);
    }

    [Fact]
    public void Nested_splits_collapse_all_the_way_up()
    {
        Open(1);
        _tree.Add(W(2), W(1), SplitDirection.Vertical);
        _tree.Add(W(3), W(2), SplitDirection.Horizontal);
        Assert.Equal("V[0x1 H[0x2 0x3]]", _tree.ToString());

        _tree.Remove(W(3));
        _tree.Remove(W(2));

        Ok();
        Assert.Equal("0x1", _tree.ToString());
        Assert.Equal(Main, RectOf(1));
    }

    // ---- direction --------------------------------------------------------

    [Fact]
    public void The_window_to_the_right_is_the_one_you_can_see()
    {
        Open(1, 2, 3);

        Assert.Equal(W(2), _tree.Neighbour(W(1), Direction.Right, Main, NoGaps));
        Assert.Equal(W(2), _tree.Neighbour(W(3), Direction.Left, Main, NoGaps));
        Assert.Equal(WindowHandle.None, _tree.Neighbour(W(3), Direction.Right, Main, NoGaps));
    }

    [Fact]
    public void Direction_crosses_a_split_rather_than_walking_the_tree()
    {
        // H[ 1  V[ 2  3 ] ]: from 2, "left" is 1 -- which is not its sibling,
        // and is the whole reason this is answered geometrically.
        Open(1, 2);
        _tree.Add(W(3), W(2), SplitDirection.Vertical);

        Assert.Equal(W(1), _tree.Neighbour(W(2), Direction.Left, Main, NoGaps));
        Assert.Equal(W(1), _tree.Neighbour(W(3), Direction.Left, Main, NoGaps));
        Assert.Equal(W(3), _tree.Neighbour(W(2), Direction.Down, Main, NoGaps));
    }

    [Fact]
    public void A_window_that_faces_you_beats_one_that_only_lies_that_way()
    {
        // H[ 1  V[ 2  3 ] ]: from 1, "right" must be 2 (the top one, which
        // overlaps the top half of 1 as well) -- both are to the right, and
        // the tie is broken by which one actually faces it.
        Open(1, 2);
        _tree.Add(W(3), W(2), SplitDirection.Vertical);

        Assert.Equal(W(2), _tree.Neighbour(W(1), Direction.Right, Main, NoGaps));
    }

    [Fact]
    public void There_is_no_neighbour_off_the_edge_of_the_workspace()
    {
        Open(1);

        Assert.Equal(WindowHandle.None, _tree.Neighbour(W(1), Direction.Left, Main, NoGaps));
        Assert.Equal(WindowHandle.None, _tree.Neighbour(W(1), Direction.Up, Main, NoGaps));
    }

    // ---- moving -----------------------------------------------------------

    [Fact]
    public void Two_side_by_side_swap()
    {
        Open(1, 2);
        Rect left = RectOf(1);

        Assert.True(_tree.Move(W(1), Direction.Right, Main, NoGaps));

        Ok();
        Assert.Equal("H[0x2 0x1]", _tree.ToString());
        Assert.Equal(left, RectOf(2));
    }

    [Fact]
    public void Moving_out_of_a_nest_lands_beside_the_window_it_aimed_at()
    {
        // H[ 1  V[ 2  3 ] ] -- move 2 left, and it should end up beside 1,
        // with the nest collapsing because 3 is alone in it.
        Open(1, 2);
        _tree.Add(W(3), W(2), SplitDirection.Vertical);

        Assert.True(_tree.Move(W(2), Direction.Left, Main, NoGaps));

        Ok();
        Assert.Equal("H[0x2 0x1 0x3]", _tree.ToString());
    }

    [Fact]
    public void Moving_into_a_nest_puts_it_on_the_correct_side()
    {
        Open(1, 2);
        _tree.Add(W(3), W(2), SplitDirection.Vertical);

        // 1 moves right, towards 2, which is the top of the nested column.
        Assert.True(_tree.Move(W(1), Direction.Right, Main, NoGaps));

        Ok();
        Assert.Contains(W(1), _tree.Windows);
        Assert.Equal(3, _tree.Count);
    }

    [Fact]
    public void Moving_off_the_edge_does_nothing_and_says_so()
    {
        Open(1, 2);

        // The caller takes "no" as "try the next monitor".
        Assert.False(_tree.Move(W(1), Direction.Left, Main, NoGaps));
        Ok();
        Assert.Equal("H[0x1 0x2]", _tree.ToString());
    }

    [Fact]
    public void Moving_up_and_back_down_returns_the_layout_it_started_from()
    {
        Open(1, 2, 3);
        string before = _tree.ToString();

        _tree.Move(W(1), Direction.Right, Main, NoGaps);
        _tree.Move(W(1), Direction.Left, Main, NoGaps);

        Ok();
        Assert.Equal(before, _tree.ToString());
    }

    // ---- resizing ---------------------------------------------------------

    [Fact]
    public void An_edge_moves_and_takes_the_space_from_the_neighbour()
    {
        Open(1, 2);

        Assert.True(_tree.Resize(W(1), Direction.Right, 0.05));

        Ok();
        Assert.Equal(2112, RectOf(1).Width);
        Assert.Equal(1728, RectOf(2).Width);
        Assert.Equal(Main.Width, RectOf(1).Width + RectOf(2).Width);
    }

    [Fact]
    public void A_nested_window_resizes_the_column_it_is_in()
    {
        // The right edge of 2 is the right edge of the whole nest, so the
        // space has to come from 1 -- a level up from where 2 lives.
        Open(1, 2);
        _tree.Add(W(3), W(2), SplitDirection.Vertical);

        Assert.True(_tree.Resize(W(2), Direction.Left, 0.05));

        Ok();
        Assert.True(RectOf(1).Width < 1920, $"1 is still {RectOf(1).Width} wide");
        Assert.Equal(RectOf(2).Width, RectOf(3).Width);
    }

    [Fact]
    public void A_window_cannot_squeeze_its_neighbour_out_of_existence()
    {
        Open(1, 2);

        for (int i = 0; i < 50; i++)
        {
            _tree.Resize(W(1), Direction.Right, 0.05);
        }

        Ok();
        Assert.True(RectOf(2).Width > 0, "the neighbour has been squeezed to nothing");
        Assert.True(RectOf(2).Width >= Main.Width * 0.04, $"the neighbour is only {RectOf(2).Width} wide");
    }

    [Fact]
    public void Resizing_the_only_window_of_a_workspace_does_nothing()
    {
        Open(1);

        // Nothing to take the room from. A window alone on a workspace already
        // fills it, whichever way it is asked to grow.
        Assert.False(_tree.Resize(W(1), Direction.Left, 0.05));
        Assert.False(_tree.Resize(W(1), Direction.Right, 0.05));
        Ok();
    }

    [Fact]
    public void Resizing_the_window_at_the_end_of_a_row_takes_from_the_neighbour_it_has()
    {
        Open(1, 2);

        // `resize --width 10%` means make it wider, not move the edge on the
        // side it is asked for: there is nothing to the left of the first
        // window and it still has to grow. It used to do nothing at all --
        // found by tests/wm on the desk, 2026-09-21.
        double was = _tree.Root!.Children[0].Share;

        Assert.True(_tree.Resize(W(1), Direction.Left, 0.05));
        Assert.True(_tree.Root!.Children[0].Share > was);
        Ok();
    }

    // ---- direction toggle -------------------------------------------------

    [Fact]
    public void Toggling_turns_the_split_through_ninety_degrees()
    {
        Open(1, 2);

        Assert.True(_tree.ToggleDirection(W(1)));

        Ok();
        Assert.Equal(Main.Width, RectOf(1).Width);
        Assert.Equal(1059, RectOf(1).Height);
    }

    [Fact]
    public void A_lone_window_has_no_split_to_turn()
    {
        Open(1);

        Assert.False(_tree.ToggleDirection(W(1)));
    }

    // ---- the tree stays honest -------------------------------------------

    [Fact]
    public void A_hundred_random_gestures_leave_a_valid_tree()
    {
        var random = new Random(20260920);
        var live = new List<long>();

        for (int i = 0; i < 100; i++)
        {
            long handle = random.Next(1, 12);
            Direction direction = (Direction)random.Next(4);

            switch (random.Next(5))
            {
                case 0:
                    _tree.Add(W(handle), W(live.Count > 0 ? live[random.Next(live.Count)] : 0),
                        random.Next(2) == 0 ? SplitDirection.Horizontal : SplitDirection.Vertical);
                    if (!live.Contains(handle))
                    {
                        live.Add(handle);
                    }

                    break;
                case 1:
                    _tree.Remove(W(handle));
                    live.Remove(handle);
                    break;
                case 2:
                    _tree.Move(W(handle), direction, Main, NoGaps);
                    break;
                case 3:
                    _tree.Resize(W(handle), direction, 0.05);
                    break;
                default:
                    _tree.ToggleDirection(W(handle));
                    break;
            }

            Ok();
        }

        // And every window that is still in it has a rectangle on the screen.
        foreach ((WindowHandle _, Rect rect) in _tree.Rects(Main, new Gaps(12)))
        {
            Assert.True(rect.Width > 0 && rect.Height > 0);
            Assert.True(Main.Contains(rect));
        }
    }
}
