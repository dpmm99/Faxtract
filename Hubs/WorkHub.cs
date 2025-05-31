using Faxtract.Interfaces;
using Faxtract.Services;
using Microsoft.AspNetCore.SignalR;

namespace Faxtract.Hubs;

public class WorkHub(IWorkProvider workProvider) : Hub
{
    public async Task RequestUpdate()
    {
        await Clients.Caller.SendAsync("UpdateStatus",
            WorkProcessor.ProcessedCount,
            workProvider.GetRemainingCount(),
            WorkProcessor.CurrentWork,
            WorkProcessor.TokensPerSecond,
            WorkProcessor.TotalTokensProcessed);
    }
}
