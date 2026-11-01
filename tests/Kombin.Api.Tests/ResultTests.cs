using Kombin.Api.Common.Results;

namespace Kombin.Api.Tests;

public class ResultTests
{
    private static readonly Error SomeError = Error.Conflict("Test.Conflict", "Something conflicts.");

    [Fact]
    public void SuccessCarriesNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void FailureCarriesTheError()
    {
        var result = Result.Failure(SomeError);

        Assert.True(result.IsFailure);
        Assert.Equal(SomeError, result.Error);
    }

    [Fact]
    public void ImplicitConversionFromErrorProducesFailure()
    {
        Result<int> result = SomeError;

        Assert.True(result.IsFailure);
        Assert.Equal("Test.Conflict", result.Error.Code);
    }

    [Fact]
    public void ImplicitConversionFromValueProducesSuccess()
    {
        Result<string> result = "hello";

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void ReadingValueOfFailedResultThrows()
    {
        Result<int> result = SomeError;

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void MatchPicksTheBranchThatApplies()
    {
        Result<int> success = 21;
        Result<int> failure = SomeError;

        Assert.Equal("ok 21", success.Match(value => $"ok {value}", error => error.Code));
        Assert.Equal("Test.Conflict", failure.Match(value => $"ok {value}", error => error.Code));
    }

    [Fact]
    public void NonGenericMatchPicksTheBranchThatApplies()
    {
        Assert.Equal("ok", Result.Success().Match(() => "ok", error => error.Code));
        Assert.Equal("Test.Conflict", Result.Failure(SomeError).Match(() => "ok", error => error.Code));
    }

    [Fact]
    public void MapTransformsSuccessAndPropagatesFailure()
    {
        Result<int> success = 20;
        Result<int> failure = SomeError;

        Assert.Equal(40, success.Map(value => value * 2).Value);

        var mappedFailure = failure.Map(value => value * 2);
        Assert.True(mappedFailure.IsFailure);
        Assert.Equal(SomeError, mappedFailure.Error);
    }

    [Theory]
    [InlineData(nameof(ErrorType.Validation), 400)]
    [InlineData(nameof(ErrorType.Unauthorized), 401)]
    [InlineData(nameof(ErrorType.Forbidden), 403)]
    [InlineData(nameof(ErrorType.NotFound), 404)]
    [InlineData(nameof(ErrorType.Conflict), 409)]
    [InlineData(nameof(ErrorType.Failure), 500)]
    public void ErrorTypeMapsToHttpStatusCode(string errorTypeName, int expectedStatusCode)
    {
        var errorType = Enum.Parse<ErrorType>(errorTypeName);

        Assert.Equal(expectedStatusCode, errorType.ToStatusCode());
    }

    [Fact]
    public void ErrorFactoriesSetTheErrorType()
    {
        Assert.Equal(ErrorType.Validation, Error.Validation("c", "d").ErrorType);
        Assert.Equal(ErrorType.Unauthorized, Error.Unauthorized("c", "d").ErrorType);
        Assert.Equal(ErrorType.Forbidden, Error.Forbidden("c", "d").ErrorType);
        Assert.Equal(ErrorType.NotFound, Error.NotFound("c", "d").ErrorType);
        Assert.Equal(ErrorType.Conflict, Error.Conflict("c", "d").ErrorType);
        Assert.Equal(ErrorType.Failure, Error.Failure("c", "d").ErrorType);
    }
}
