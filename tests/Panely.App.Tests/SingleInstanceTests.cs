using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Panely.App.Shell;

namespace Panely.App.Tests;

/// <summary>실제 named pipe로 single-instance 서버를 검사한다. appId가 테스트마다 달라 실행 중인 Panely와 간섭하지 않는다.</summary>
public sealed class SingleInstanceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly SingleInstance _instance = new("PanelyTest-" + Guid.NewGuid().ToString("N"));
    private readonly BlockingCollection<IReadOnlyList<string>> _received = [];

    public SingleInstanceTests() => _instance.OpenRequested += _received.Add;

    private IReadOnlyList<string> NextRequest()
    {
        Assert.True(_received.TryTake(out var request, Wait), "요청이 전달되지 않았습니다.");
        return request!;
    }

    [Fact]
    public void Requests_before_ready_are_held_then_delivered_in_order()
    {
        _instance.StartServer();
        Assert.True(_instance.SendToPrimary([@"C:\first.cbz"], Wait));
        Assert.True(_instance.SendToPrimary([@"C:\second.cbz"], Wait));
        Thread.Sleep(300);
        Assert.Empty(_received);

        _instance.MarkReady();
        Assert.Equal(@"C:\first.cbz", NextRequest()[0]);
        Assert.Equal(@"C:\second.cbz", NextRequest()[0]);

        // 준비 뒤에는 바로 전달
        Assert.True(_instance.SendToPrimary([@"C:\third.cbz"], Wait));
        Assert.Equal(@"C:\third.cbz", NextRequest()[0]);
    }

    [Fact]
    public void Held_requests_are_bounded_and_keep_the_newest()
    {
        for (var i = 0; i < 40; i++) _instance.Deliver([$@"C:\{i}.cbz"]);
        _instance.MarkReady();
        Assert.Equal(32, _received.Count);
        Assert.Equal(@"C:\8.cbz", NextRequest()[0]);
    }

    [Fact]
    public async Task Send_retries_until_the_primary_starts_its_server()
    {
        var send = Task.Run(() => _instance.SendToPrimary([@"C:\late.cbz"], Wait));
        await Task.Delay(500);
        Assert.False(send.IsCompleted);
        _instance.StartServer();
        _instance.MarkReady();
        Assert.True(await send);
        Assert.Equal(@"C:\late.cbz", NextRequest()[0]);
    }

    [Fact]
    public void Send_gives_up_after_its_budget_when_no_primary_answers()
        => Assert.False(_instance.SendToPrimary([@"C:\x.cbz"], TimeSpan.FromMilliseconds(300)));

    [Fact]
    public void Launch_without_paths_reaches_the_primary_as_an_empty_request()
    {
        _instance.StartServer();
        _instance.MarkReady();
        Assert.True(_instance.SendToPrimary([], Wait));
        Assert.Empty(NextRequest());
    }

    [Theory]
    [InlineData("""{"Paths":[]}""", true, 0)]
    [InlineData("""{"Paths":["C:\\a.cbz"]}""", true, 1)]
    [InlineData("""{"Paths":["relative\\a.cbz"]}""", false, 0)]
    [InlineData("""{"Paths":null}""", false, 0)]
    [InlineData("""{}""", false, 0)]
    [InlineData("""not json""", false, 0)]
    public void Only_empty_or_valid_path_lists_are_requests(string json, bool accepted, int count)
    {
        Assert.Equal(accepted, SingleInstance.TryParseRequest(Encoding.UTF8.GetBytes(json), out var paths));
        Assert.Equal(count, paths.Count);
    }

    [Fact]
    public void A_client_that_never_writes_does_not_block_the_next_one()
    {
        using var quick = new SingleInstance("PanelyTest-" + Guid.NewGuid().ToString("N")) { ReadTimeout = TimeSpan.FromMilliseconds(200) };
        var received = new BlockingCollection<IReadOnlyList<string>>();
        quick.OpenRequested += received.Add;
        quick.StartServer();
        quick.MarkReady();

        using var silent = new NamedPipeClientStream(".", quick.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        silent.Connect(5000);

        Assert.True(quick.SendToPrimary([@"C:\next.cbz"], Wait));
        Assert.True(received.TryTake(out var request, Wait));
        Assert.Equal(@"C:\next.cbz", request![0]);
    }

    [Fact]
    public void Server_creation_failures_back_off_and_recover()
    {
        // 같은 이름의 단일 인스턴스 pipe를 먼저 잡아 NamedPipeServerStream 생성자가 계속 실패하게 한다
        var blocker = new NamedPipeServerStream(_instance.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _instance.StartServer();
        _instance.MarkReady();
        Thread.Sleep(1000);
        Assert.InRange(_instance.ServerFaults, 1, 8); // 100·200·400·800ms 간격. 간격이 없으면 수천 회

        blocker.Dispose();
        Assert.True(_instance.SendToPrimary([@"C:\after.cbz"], Wait));
        Assert.Equal(@"C:\after.cbz", NextRequest()[0]);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(4, 800)]
    [InlineData(7, 5000)]
    [InlineData(100000, 5000)]
    public void Fault_delay_doubles_up_to_a_cap(int faults, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), SingleInstance.FaultDelay(faults));

    public void Dispose()
    {
        _instance.Dispose();
        _received.Dispose();
    }
}
