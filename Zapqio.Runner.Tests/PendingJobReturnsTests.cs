using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

/// <summary>
/// Wynik zadania nie może przepaść razem z połączeniem. Reguła: zostaje do ponowienia, dopóki
/// platforma nie da znaku życia po jego wysłaniu - nieudana wysyłka i wysyłka w martwe gniazdo
/// wyglądają dla runnera tak samo. Przy kilku zadaniach naraz każdy wynik ma własny wpis, a znak
/// życia potwierdza tylko te wysłane przed nim.
/// </summary>
public class PendingJobReturnsTests
{
    private static MessageJobReturn Result(MessageResponseStatus status = MessageResponseStatus.OK) => new()
    {
        Id = Guid.NewGuid(),
        AttemptId = Guid.NewGuid(),
        Status = status,
        Data = "{\"ok\":true}"
    };

    [Fact]
    public void NothingPending_Initially()
    {
        Assert.Empty(new PendingJobReturns().PeekAll());
    }

    [Fact]
    public void FailedSend_IsKeptForResend()
    {
        var pending = new PendingJobReturns();
        var result = Result(MessageResponseStatus.ERROR);

        pending.MarkFailed(result);

        Assert.Same(result, Assert.Single(pending.PeekAll()));
    }

    /// <summary>
    /// Sukces wysyłki nic nie dowodzi: zapis do martwego gniazda potrafi się „udać". Dopiero wiadomość
    /// od platformy po wysyłce zwalnia wynik.
    /// </summary>
    [Fact]
    public void SuccessfulSend_IsKeptUntilThePlatformShowsSignsOfLife()
    {
        var pending = new PendingJobReturns();
        var result = Result();

        pending.MarkSent(result, sentSeq: 10);
        Assert.Same(result, Assert.Single(pending.PeekAll()));

        pending.Confirm(seenSeq: 11);
        Assert.Empty(pending.PeekAll());
    }

    [Fact]
    public void Confirm_WithoutAnythingPending_IsHarmless()
    {
        var pending = new PendingJobReturns();

        pending.Confirm(seenSeq: 5);

        Assert.Empty(pending.PeekAll());
    }

    /// <summary>Znak życia potwierdza tylko wysyłki sprzed niego - wynik wysłany później dalej czeka.</summary>
    [Fact]
    public void Confirm_ReleasesOnlyResultsSentBeforeTheSign()
    {
        var pending = new PendingJobReturns();
        var earlier = Result();
        var later = Result();

        pending.MarkSent(earlier, sentSeq: 10);
        pending.MarkSent(later, sentSeq: 12);

        pending.Confirm(seenSeq: 11);

        Assert.Same(later, Assert.Single(pending.PeekAll()));
    }

    [Fact]
    public void Confirm_LeavesFailedSendsAlone()
    {
        var pending = new PendingJobReturns();
        var failed = Result(MessageResponseStatus.ERROR);
        var sent = Result();

        pending.MarkFailed(failed);
        pending.MarkSent(sent, sentSeq: 3);

        pending.Confirm(seenSeq: 4);

        Assert.Same(failed, Assert.Single(pending.PeekAll()));
    }

    [Fact]
    public void PeekAll_KeepsTheOrderOfFirstAppearance_AndReplacesByAttempt()
    {
        var pending = new PendingJobReturns();
        var first = Result();
        var second = Result();

        pending.MarkFailed(first);
        pending.MarkFailed(second);
        // Ponowna wysyłka tego samego wyniku podmienia wpis, nie dokłada drugiego.
        pending.MarkSent(first, sentSeq: 7);

        var all = pending.PeekAll();
        Assert.Equal(2, all.Count);
        Assert.Same(first, all[0]);
        Assert.Same(second, all[1]);
    }
}
