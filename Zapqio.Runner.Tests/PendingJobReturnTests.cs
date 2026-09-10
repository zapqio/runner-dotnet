using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

/// <summary>
/// Wynik zadania nie może przepaść razem z połączeniem. Reguła: zostaje do ponowienia, dopóki
/// platforma nie da znaku życia po jego wysłaniu - nieudana wysyłka i wysyłka w martwe gniazdo
/// wyglądają dla runnera tak samo.
/// </summary>
public class PendingJobReturnTests
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
        Assert.Null(new PendingJobReturn().Peek());
    }

    [Fact]
    public void FailedSend_IsKeptForResend()
    {
        var pending = new PendingJobReturn();
        var result = Result(MessageResponseStatus.ERROR);

        pending.MarkFailed(result);

        Assert.Same(result, pending.Peek());
    }

    /// <summary>
    /// Sukces wysyłki nic nie dowodzi: zapis do martwego gniazda potrafi się „udać". Dopiero wiadomość
    /// od platformy po wysyłce zwalnia wynik.
    /// </summary>
    [Fact]
    public void SuccessfulSend_IsKeptUntilThePlatformShowsSignsOfLife()
    {
        var pending = new PendingJobReturn();
        var result = Result();

        pending.MarkSent(result);
        Assert.Same(result, pending.Peek());

        pending.Confirm();
        Assert.Null(pending.Peek());
    }

    [Fact]
    public void Confirm_WithoutAnythingPending_IsHarmless()
    {
        var pending = new PendingJobReturn();

        pending.Confirm();

        Assert.Null(pending.Peek());
    }

    /// <summary>Runner ma jedno zadanie naraz, więc nowszy wynik zastępuje starszy zamiast się za nim ustawiać.</summary>
    [Fact]
    public void NewerResult_ReplacesTheOlderOne()
    {
        var pending = new PendingJobReturn();
        var older = Result();
        var newer = Result();

        pending.MarkFailed(older);
        pending.MarkSent(newer);

        Assert.Same(newer, pending.Peek());
    }

    /// <summary>Ponowienie po powrocie: wysłany ponownie wynik dalej czeka na znak życia, a nie znika od razu.</summary>
    [Fact]
    public void ResentResult_StillWaitsForConfirmation()
    {
        var pending = new PendingJobReturn();
        var result = Result();
        pending.MarkFailed(result);

        var toResend = pending.Peek()!;
        pending.MarkSent(toResend);

        Assert.Same(result, pending.Peek());
        pending.Confirm();
        Assert.Null(pending.Peek());
    }
}
