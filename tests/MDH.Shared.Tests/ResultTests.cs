using FluentAssertions;
using MDH.Shared.Common;
using Xunit;

namespace MDH.Shared.Tests;

public class ResultTests
{
    [Fact]
    public void Success_IsSuccessTrue_ValueAvailable()
    {
        var result = Result<string>.Success("hello");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello");
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_IsSuccessFalse_ErrorAvailable()
    {
        var result = Result<string>.Failure("something went wrong");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("something went wrong");
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Match_OnSuccess_CallsSuccessFunc()
    {
        var result = Result<int>.Success(42);

        var output = result.Match(
            onSuccess: v => $"value={v}",
            onFailure: e => $"error={e}");

        output.Should().Be("value=42");
    }

    [Fact]
    public void Match_OnFailure_CallsFailureFunc()
    {
        var result = Result<int>.Failure("not found");

        var output = result.Match(
            onSuccess: v => $"value={v}",
            onFailure: e => $"error={e}");

        output.Should().Be("error=not found");
    }

    [Fact]
    public void NonGenericResult_Success_IsSuccessTrue()
    {
        var result = Result.Success();
        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void NonGenericResult_Failure_IsSuccessFalse()
    {
        var result = Result.Failure("bad input");
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("bad input");
    }
}
