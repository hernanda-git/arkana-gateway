using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Mvc;
namespace Arkana.Gateway.Api.Endpoints;
public static class ProviderAccountEndpoints
{
 public static void MapProviderAccountEndpoints(this WebApplication app)
 {
  var g=app.MapGroup("/admin/provider-accounts").WithTags("Admin","Provider Accounts").RequireAuthorization("AdminApi");
  g.MapGet("",async(ProviderAccountDashboardFacade f,CancellationToken ct)=>Results.Ok(await f.ListAsync(ct)));
  g.MapGet("/{id:guid}",async(Guid id,ProviderAccountDashboardFacade f,CancellationToken ct)=>await Execute(()=>f.GetAsync(id,ct),true));
  g.MapPost("/{id:guid}/start",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>Mutate(id,r,h,f,false,ct));
  g.MapPost("/{id:guid}/reconnect",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>Mutate(id,r,h,f,true,ct));
  g.MapGet("/{id:guid}/operations/{operationId:guid}",async(Guid id,Guid operationId,ProviderAccountDashboardFacade f,CancellationToken ct)=>await Execute(()=>f.OperationAsync(id,operationId,ct),true));
  g.MapGet("/{id:guid}/operations",async(Guid id,ProviderAccountDashboardFacade f,CancellationToken ct)=>Results.Ok(await f.OperationsAsync(id,ct)));
  g.MapPost("/{id:guid}/operations/{operationId:guid}/recover-delete",async(Guid id,Guid operationId,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>{try{var result=await f.RecoverDeleteAsync(id,operationId,h.HttpContext.User.Identity?.Name??"admin",Key(h),ct);return Results.Ok(result);}catch(Exception e){return Failure(e);}});
  g.MapPost("/{id:guid}/callback",(Guid id,[FromBody]ProviderAccountCallbackRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>Callback(id,r,h,f,ct));
  g.MapPost("/{id:guid}/callback/manual",(Guid id,[FromBody]ProviderAccountCallbackRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>Callback(id,r,h,f,ct));
  g.MapPost("/{id:guid}/test",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>Probe(id,r,h,f,ct));
  g.MapPost("/{id:guid}/probe",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>Probe(id,r,h,f,ct));
  g.MapPost("/{id:guid}/cancel",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>Cancel(id,r,h,f,ct));
  g.MapPost("/{id:guid}/disable",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>State(id,r,h,f,false,false,ct));
  g.MapPost("/{id:guid}/drain",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>State(id,r,h,f,false,true,ct));
  g.MapPost("/{id:guid}/enable",(Guid id,[FromBody]ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct)=>State(id,r,h,f,true,false,ct));
  g.MapDelete("/{id:guid}",async(Guid id,HttpRequest h,[FromBody]ProviderAccountSlotRequest r,ProviderAccountDashboardFacade f,CancellationToken ct)=>{try{Check(h);var ok=await f.DeleteAsync(id,r.Slot,r.StableAuthId,h.HttpContext.User.Identity?.Name??"admin",Key(h),Version(h),ct);return ok?Results.NoContent():Results.Conflict(new{error="Tombstone verification failed."});}catch(Exception e){return Failure(e);}});
  g.MapPost("/{id:guid}/cleanup-pending",async(Guid id,ProviderAccountDashboardFacade f,CancellationToken ct)=>await Execute(()=>f.CleanupDeletedAccountPendingFlowsAsync(id,ct),true));
 }
 private static async Task<IResult> Mutate(Guid id,ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,bool reconnect,CancellationToken ct){try{Check(h);return Results.Ok(await f.StartAsync(id,r.Slot,reconnect,Key(h),Version(h),ct));}catch(Exception e){return Failure(e);}}
 private static async Task<IResult> Callback(Guid id,ProviderAccountCallbackRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct){try{Check(h);return Results.Ok(await f.CallbackAsync(id,r.Slot,r.RedirectUrl,Key(h),Version(h),ct));}catch(Exception e){return Failure(e);}}
 private static async Task<IResult> Probe(Guid id,ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct){try{Check(h);return Results.Ok(await f.ProbeAsync(id,r.Slot,Key(h),Version(h),ct));}catch(Exception e){return Failure(e);}}
 private static async Task<IResult> Cancel(Guid id,ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,CancellationToken ct){try{Check(h);return Results.Ok(await f.CancelAsync(id,r.Slot,r.StableAuthId??throw new ArgumentException("StableAuthId is required."),Key(h),Version(h),ct));}catch(Exception e){return Failure(e);}}
 private static async Task<IResult> State(Guid id,ProviderAccountSlotRequest r,HttpRequest h,ProviderAccountDashboardFacade f,bool enable,bool drain,CancellationToken ct){try{Check(h);return Results.Ok(await f.SetStateAsync(id,r.Slot,r.StableAuthId??throw new ArgumentException("StableAuthId is required."),enable,drain,Key(h),Version(h),ct));}catch(Exception e){return Failure(e);}}
 private static void Check(HttpRequest h){_ = Key(h);_ = Version(h);}
 private static string Key(HttpRequest h)=>h.Headers["Idempotency-Key"].FirstOrDefault()??throw new ArgumentException("Idempotency-Key header is required.");
 private static Guid Version(HttpRequest h){var x=h.Headers.IfMatch.FirstOrDefault()?.Trim('"');return Guid.TryParse(x,out var v)?v:throw new ArgumentException("If-Match must contain the account version GUID.");}
 private static async Task<IResult> Execute<T>(Func<Task<T?>> a,bool nf){try{var v=await a();return v is null&&nf?Results.NotFound():Results.Ok(v);}catch(Exception e){return Failure(e);}}
 private static IResult Failure(Exception e)=>e switch{KeyNotFoundException=>Results.NotFound(new{error=e.Message}),ArgumentException=>Results.BadRequest(new{error=e.Message}),ProviderAccountPreconditionException=>Results.StatusCode(412),ProviderAccountIdempotencyConflictException=>Results.Conflict(new{error="Idempotency key conflicts with an existing operation."}),HttpRequestException=>Results.Json(new{error="Broker operation failed."},statusCode:502),_=>Results.Problem("Provider account operation failed.",statusCode:409)};
}
internal static class ResultExtensions{public static IResult NoContentIf(this IResult _,bool ok)=>ok?Results.NoContent():Results.Conflict(new{error="Tombstone verification failed."});}
