using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class ElevatedWorkerTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ValidTypedRequest_IsAcceptedButProductionWriteRemainsLocked()
    {
        var request = new ElevatedWorkerRequest(
            ElevatedWorker.SchemaVersion,
            Guid.NewGuid(),
            WorkerOperationKind.Apply,
            [new("mmcss.system-responsiveness", null, new(true, 20), new(true, 10), ChangeSource.Manual)]);
        using var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(
            new StringReader(JsonSerializer.Serialize(request, Options)), output, CancellationToken.None);
        var response = JsonSerializer.Deserialize<ElevatedWorkerResponse>(output.ToString(), Options)!;

        Assert.Equal(3, exitCode);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.False(response.Success);
        Assert.Contains("writes remain locked", response.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[],\"Command\":\"cmd.exe\"}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"arbitrary.registry.path\",\"TargetId\":null,\"Expected\":{\"Exists\":false,\"Value\":0},\"Desired\":{\"Exists\":true,\"Value\":1},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Changes\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":1,\"Changes\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Unknown\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":20},\"Desired\":{\"Exists\":true,\"Value\":10},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[null]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":20,\"Path\":\"HKLM\"},\"Desired\":{\"Exists\":true,\"Value\":10},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":20},\"Desired\":{\"Exists\":true,\"Value\":10}}]}")]
    public async Task UnlistedOrStructurallyUnknownInput_IsRejected(string json)
    {
        using var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(new StringReader(json), output, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Rejected typed request", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequest_IsRejected()
    {
        using var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(
            new StringReader(new string('x', ElevatedWorker.MaximumRequestCharacters + 1)),
            output,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
    }
}
