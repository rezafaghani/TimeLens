using TimeLens.API.Controllers;
using TimeLens.API.Infrastructure.Services;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace TimeLens.UnitTest.Controllers;

public class ExecutionControllerTests
{
    [Fact]
    public void Plugins_ReturnsEmptyListWhenApiHasNoLocalPlugins()
    {
        var result = Controller().Plugins(ExecutionCategories.Validation);

        var ok = Assert.IsType<OkObjectResult>(result);
        var plugins = Assert.IsType<List<ExecutionPluginDto>>(ok.Value);
        Assert.Empty(plugins);
    }

    [Fact]
    public async Task UpsertDefinition_RejectsUnavailablePlugin()
    {
        var request = new UpsertExecutionDefinitionRequest(
            null,
            "Definition",
            null,
            true,
            "*/15 * * * *",
            "UTC",
            "now-24h",
            "now",
            null,
            null,
            null,
            [new UpsertExecutionDefinitionTargetRequest("dataset", "dataset-1", null)],
            [new UpsertExecutionDefinitionPluginRequest(null, "timelens.analytics.unknown", null, true, null, null, null)]);

        var result = await Controller().UpsertDefinition(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpsertDefinition_DefaultsValidationWindowWhenOmitted()
    {
        var repository = new Mock<IExecutionRepository>();
        repository.Setup(x => x.UpsertDefinitionAsync(It.IsAny<UpsertExecutionDefinitionRequest>(), CancellationToken.None))
            .ReturnsAsync((UpsertExecutionDefinitionRequest request, CancellationToken _) => new ExecutionDefinitionDto(
                "definition-1",
                request.Name,
                request.Description ?? string.Empty,
                request.Enabled,
                request.CronExpression,
                request.TimeZone,
                request.WindowStartExpression,
                request.WindowEndExpression,
                request.MaxParallelism ?? 4,
                request.TimeoutSeconds ?? 300,
                JsonSerializer.SerializeToElement(new { }),
                [],
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null));
        var controller = new ExecutionController(
            new ExecutionPluginRegistry([new FakeValidationPlugin()]),
            new ExecutionRuntime(new ExecutionPluginRegistry([new FakeValidationPlugin()]), Mock.Of<IMarketDataReader>()),
            repository.Object);
        var request = new UpsertExecutionDefinitionRequest(
            null,
            "Validation",
            null,
            true,
            "*/15 * * * *",
            "UTC",
            null,
            null,
            null,
            null,
            null,
            [new UpsertExecutionDefinitionTargetRequest("dataset", "dataset-1", null)],
            [new UpsertExecutionDefinitionPluginRequest(null, "timelens.validation.fake", null, true, null, null, null)]);

        var result = await controller.UpsertDefinition(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(x => x.UpsertDefinitionAsync(
            It.Is<UpsertExecutionDefinitionRequest>(saved =>
                saved.WindowStartExpression == "now-24h" && saved.WindowEndExpression == "now"),
            CancellationToken.None));
    }

    private static ExecutionController Controller() =>
        new(new ExecutionPluginRegistry(Plugins()),
            new ExecutionRuntime(
                new ExecutionPluginRegistry(Plugins()),
                Mock.Of<IMarketDataReader>()),
            Mock.Of<IExecutionRepository>());

    private static List<IExecutionPlugin> Plugins() => [];

    private sealed class FakeValidationPlugin : IExecutionPlugin
    {
        public ExecutionPluginDto Metadata { get; } = new(
            "timelens.validation.fake",
            "Fake validation",
            "",
            ExecutionCategories.Validation,
            1,
            ["dataset"],
            [],
            [],
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            "quality.finding");

        public Task<List<ExecutionStepResultDto>> ExecuteAsync(ExecutionPluginContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<List<ExecutionStepResultDto>>([]);
    }
}
