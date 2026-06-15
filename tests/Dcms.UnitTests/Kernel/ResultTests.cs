using Dcms.Shared.Kernel.Primitives;

namespace Dcms.UnitTests.Kernel;

public class ResultTests
{
    [Fact]
    public void Success_carries_value()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_carries_error_and_throws_on_value_access()
    {
        var error = Error.NotFound("content.missing", "Content item not found.");
        var result = Result.Failure<int>(error);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_requires_an_error()
    {
        var act = () => Result.Failure(Error.None);
        act.Should().Throw<InvalidOperationException>();
    }
}
