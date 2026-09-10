using Zapqio.Runner.Core;

namespace Zapqio.Runner.Tests;

/// <summary>
/// Kontrakt kontekstu zadania dla modułów: widoczny wyłącznie w czasie wykonania metody, także w
/// zadaniach, które metoda sama uruchomi, i nigdy nie przecieka do kolejnego zadania.
/// </summary>
public class JobContextTests
{
    private static JobContext Context(string method = "resize-image") =>
        new(Guid.NewGuid(), Guid.NewGuid(), method);

    [Fact]
    public void Current_IsNull_OutsideOfAJob()
    {
        Assert.Null(JobContext.Current);
    }

    [Fact]
    public void Begin_ExposesTheContext_AndDisposeClearsIt()
    {
        var context = Context();

        using (JobContext.Begin(context))
        {
            Assert.Same(context, JobContext.Current);
            Assert.Equal(context.JobId, JobContext.Current!.JobId);
            Assert.Equal(context.AttemptId, JobContext.Current.AttemptId);
            Assert.Equal("resize-image", JobContext.Current.MethodName);
        }

        Assert.Null(JobContext.Current);
    }

    /// <summary>
    /// Host woła metodę przez Task.Run, a metoda może sama rozpinać pracę na wątki - kontekst ma być
    /// widoczny wszędzie tam, gdzie sięga jej przepływ wykonania.
    /// </summary>
    [Fact]
    public async Task Context_FlowsIntoTasksStartedInsideTheMethod()
    {
        var context = Context();

        using (JobContext.Begin(context))
        {
            var seen = await Task.Run(async () =>
            {
                await Task.Delay(1);
                return JobContext.Current;
            });

            Assert.Same(context, seen);
        }
    }

    [Fact]
    public void Nested_Begin_RestoresThePreviousContext()
    {
        var outer = Context("outer");
        var inner = Context("inner");

        using (JobContext.Begin(outer))
        {
            using (JobContext.Begin(inner))
            {
                Assert.Same(inner, JobContext.Current);
            }

            Assert.Same(outer, JobContext.Current);
        }

        Assert.Null(JobContext.Current);
    }

    /// <summary>Drugie zadanie nie może odziedziczyć kontekstu pierwszego - to byłby zły klucz idempotencji.</summary>
    [Fact]
    public void ContextOfOneJob_DoesNotLeakIntoTheNext()
    {
        var first = Context("first");
        var second = Context("second");

        using (JobContext.Begin(first)) { }
        Assert.Null(JobContext.Current);

        using (JobContext.Begin(second))
        {
            Assert.Same(second, JobContext.Current);
        }
    }

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        var scope = JobContext.Begin(Context());

        scope.Dispose();
        scope.Dispose();

        Assert.Null(JobContext.Current);
    }

    [Fact]
    public void Begin_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => JobContext.Begin(null!));
    }
}
