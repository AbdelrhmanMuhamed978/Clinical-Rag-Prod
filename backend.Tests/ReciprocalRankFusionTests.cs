using ClinicalRag.Api.Services;
using Xunit;

namespace ClinicalRag.Tests;

public class ReciprocalRankFusionTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public void Ranks_from_both_legs_are_added_with_k_sixty_not_incompatible_raw_scores()
    {
        var result = ReciprocalRankFusion.Fuse([new(A, .99), new(B, .9)], [new(B, 1), new(C, 10000)], 3);
        Assert.Equal(new[] { B, A, C }, result.Select(hit => hit.Id));
        Assert.Equal(1.0 / 62 + 1.0 / 61, result[0].Score, 12);
        Assert.Equal(1.0 / 61, result[1].Score, 12);
        Assert.Equal(1.0 / 62, result[2].Score, 12);
    }

    [Fact]
    public void Ties_are_deterministic_and_only_the_top_candidate_limit_is_retained()
    {
        var result = ReciprocalRankFusion.Fuse([new(B, 1)], [new(A, .1)], 1);
        Assert.Equal(A, Assert.Single(result).Id);
    }

    [Fact]
    public void A_duplicate_id_in_one_leg_cannot_gain_an_extra_vote_or_consume_a_rank()
    {
        var result = ReciprocalRankFusion.Fuse([new(A, 10), new(A, 9), new(B, 8)], [], 3);
        Assert.Equal(2, result.Count);
        Assert.Equal(1.0 / 61, result[0].Score, 12);
        Assert.Equal(1.0 / 62, result[1].Score, 12);
    }

    [Fact]
    public void An_empty_leg_keeps_the_other_leg_and_no_results_are_invented()
    {
        Assert.Equal(C, Assert.Single(ReciprocalRankFusion.Fuse([], [new(C, 2)], 30)).Id);
        Assert.Empty(ReciprocalRankFusion.Fuse([], [], 30));
        Assert.Empty(ReciprocalRankFusion.Fuse([new(A, 1)], [new(A, 1)], 0));
    }
}
