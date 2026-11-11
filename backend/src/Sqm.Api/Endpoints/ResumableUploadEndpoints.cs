using Microsoft.AspNetCore.Mvc;
using Sqm.Api.Auth;
using Sqm.Api.Infrastructure;
using Sqm.Application.DataImport;
using Sqm.Application.Identity;
using Sqm.Infrastructure.DataImport;
using tusdotnet;

namespace Sqm.Api.Endpoints;

/// <summary>Resumable uploads: the tus protocol for the bytes, then the same intake as any upload (ADR-018).</summary>
public static class ResumableUploadEndpoints
{
    /// <summary>Registers the tus endpoint and the call that completes an upload.</summary>
    public static IEndpointRouteBuilder MapResumableUploadEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // tus: create (POST), send (PATCH), ask how much arrived (HEAD), give up (DELETE). Who may
        // create one for which source is checked inside, per upload - see ResumableUploads.
        app.MapTus(
                ResumableUploads.RoutePattern,
                http => Task.FromResult(http.RequestServices.GetRequiredService<ResumableUploads>().Configuration))
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportView)
            .DisableAntiforgery()
            .WithMetadata(new DisableRequestSizeLimitAttribute());

        app.MapPost(ResumableUploads.RoutePattern + "/{uploadId}/complete", CompleteAsync)
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.ImportView)
            .WithTags("Imports")
            .WithName("CompleteResumableUpload")
            .WithSummary("Hands a fully arrived resumable upload to the import intake: queued, or already known.");

        return app;
    }

    /// <summary>
    /// Takes the finished upload into the store and through the intake - answering exactly as the
    /// single-request upload does: 201 with the queued job, 409 with the import it duplicates, 202
    /// when it waits for its date, and for an archive 200 with what became of each file in it.
    /// </summary>
    private static async Task<IResult> CompleteAsync(
        string uploadId,
        ResumableUploads uploads,
        ImportArrivals arrivals,
        IAuditLog audit,
        HttpContext http,
        CancellationToken ct)
    {
        var user = CurrentUser.Require(http);

        if (!uploads.IsOwnedBy(uploadId, user.UserId)
            || !await uploads.Store.FileExistAsync(uploadId, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        var length = await uploads.Store.GetUploadLengthAsync(uploadId, ct).ConfigureAwait(false);
        var offset = await uploads.Store.GetUploadOffsetAsync(uploadId, ct).ConfigureAwait(false);

        if (length is null || offset != length)
        {
            return Results.Problem(
                title: "Not all of the file has arrived",
                detail: $"{offset:N0} of {length:N0} bytes are here; send the rest, then complete it.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (await uploads.GetDeclaredAsync(uploadId, ct).ConfigureAwait(false) is not var (source, fileName))
        {
            return Results.Problem(
                title: "No source or name",
                detail: "The upload was created without naming its source and file.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Asked again at the end: a permission withdrawn while the file was arriving is withdrawn.
        var refusal = await ImportEndpoints.RefuseUploadAsync(source, http, audit, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        // A CSV is moved into the store - the pieces gathered on its volume for that. A gzip or an
        // archive is unpacked from where it gathered, and what it held is what is stored.
        ArrivalReport report;
        try
        {
            report = await arrivals
                .TakeFileAsync(
                    source, fileName, uploads.DataPath(uploadId), adopt: true,
                    ImportEndpoints.CurrentActor(http), http.TraceIdentifier, saidDate: null, ct)
                .ConfigureAwait(false);
        }
        catch (FileRefusedException ex)
        {
            // Refused for what it is: sending the same bytes again would be refused again, so
            // nothing of the upload is kept to be completed a second time.
            await uploads.ForgetAsync(uploadId, CancellationToken.None).ConfigureAwait(false);
            return ImportEndpoints.Refused(ex);
        }

        // Off the request's token from here on: the file is in the store, so a client that leaves
        // now must not stop the upload's own pieces from being cleared.
        await uploads.ForgetAsync(uploadId, CancellationToken.None).ConfigureAwait(false);

        return ImportEndpoints.RespondToArrival(report);
    }
}
