# Real-Time & Admin UI

## SignalR for Real-Time Streaming

Agent execution and AI responses stream in real-time to connected clients:

```csharp
// Hub for agent progress streaming
public class AgentHub : Hub
{
    public async Task ExecuteAgent(string agentId, string input)
    {
        var progress = await _agentService.ExecuteAsync(agentId, input,
            onToken: token => Clients.Caller.SendAsync("onToken", token),
            onProgress: msg => Clients.Caller.SendAsync("onProgress", msg));

        await Clients.Caller.SendAsync("onComplete", progress);
    }
}
```

## Blazor Server Admin Dashboard

Built with **MudBlazor** for professional UI components:

```csharp
@page "/dashboard"
@inject NavigationManager Navigation
@implements IAsyncDisposable

<MudContainer MaxWidth="MaxWidth.ExtraExtraLarge">
    <MudGrid>
        <MudItem xs="12" md="3">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.subtitle2">Total Requests</MudText>
                <MudText Typo="Typo.h4">@stats.TotalRequests.ToString("N0")</MudText>
            </MudPaper>
        </MudItem>
        <MudItem xs="12" md="3">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.subtitle2">Tokens Used</MudText>
                <MudText Typo="Typo.h4">@stats.TotalTokens.ToString("N0")</MudText>
            </MudPaper>
        </MudItem>
        <MudItem xs="12" md="3">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.subtitle2">Total Cost</MudText>
                <MudText Typo="Typo.h4">@stats.TotalCost.ToString("C4")</MudText>
            </MudPaper>
        </MudItem>
        <MudItem xs="12" md="3">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.subtitle2">Active Agents</MudText>
                <MudText Typo="Typo.h4">@stats.ActiveAgents</MudText>
            </MudPaper>
        </MudItem>
    </MudGrid>

    <MudTable Items="@usageData" Hover="true" Striped="true">
        <HeaderContent>
            <MudTh>Provider</MudTh>
            <MudTh>Model</MudTh>
            <MudTh>Tokens</MudTh>
            <MudTh>Cost ($)</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Provider</MudTd>
            <MudTd>@context.Model</MudTd>
            <MudTd>@context.Tokens.ToString("N0")</MudTd>
            <MudTd>@context.Cost.ToString("C4")</MudTd>
        </RowTemplate>
    </MudTable>
</MudContainer>

@code {
    private HubConnection hubConnection;
    private DashboardStats stats = new();
    private List<UsageRecord> usageData = new();

    protected override async Task OnInitializedAsync()
    {
        hubConnection = new HubConnectionBuilder()
            .WithUrl(Navigation.ToAbsoluteUri("/hubs/dashboard"))
            .WithAutomaticReconnect()
            .Build();

        hubConnection.On<UsageRecord>("UsageUpdate", record =>
        {
            usageData.Add(record);
            StateHasChanged();
        });

        await hubConnection.StartAsync();
        await LoadInitialStats();
    }
}
```

## Real-Time Data Flow

```mermaid
sequenceDiagram
    participant Client as Browser Dashboard
    participant Blazor as Blazor Server
    participant Hub as SignalR Hub
    participant Gateway as Gateway API
    participant Agent as Agent Runtime

    Client->>Blazor: Request Dashboard
    Blazor->>+Hub: Connect SignalR
    Hub-->>Blazor: Connected

    Gateway->>+Agent: Execute Agent
    Agent-->>Gateway: Progress Update
    Gateway->>Hub: SendAgentProgress
    Hub->>Blazor: onProgress callback
    Blazor->>Client: UI Update (auto)

    Agent-->>Gateway: Token Chunk
    Gateway->>Hub: SendToken
    Hub->>Blazor: onToken callback
    Blazor->>Client: Real-time token display

    Agent-->>Gateway: Complete
    Gateway->>Hub: SendComplete
    Hub->>Blazor: onComplete callback
    Blazor->>Client: Show final result
```

## MudBlazor Components Used

| Component | Use Case |
|-----------|----------|
| MudTable | Agent list, usage history, audit log |
| MudChart | Token usage trends, cost over time |
| MudCard | Dashboard stat cards |
| MudDialog | Agent configuration, confirmations |
| MudSnackbar | Notifications for events |
| MudTextField | API key input, prompt editing |
| MudSelect | Provider/model selection |
| MudProgressLinear | Agent execution progress |
