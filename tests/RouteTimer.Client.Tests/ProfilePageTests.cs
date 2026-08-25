using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Pages;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Profile;

namespace RouteTimer.Client.Tests;

public sealed class ProfilePageTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public ProfilePageTests() => Services.AddSingleton<IRouteTimerApiClient>(api);

    [Fact]
    public void Profile_loads_existing_values_and_disables_duplicate_save_after_success()
    {
        api.OnGetProfileAsync = _ => Task.FromResult<ProfileResponse?>(new ProfileResponse(71.3, 8.4));
        api.OnUpdateProfileAsync = (request, _) => Task.FromResult(new ProfileResponse(request.RiderWeightKg, request.BikeAndEquipmentWeightKg));

        var cut = Render<Profile>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("71.3", cut.Find("#rider-weight").GetAttribute("value"));
            Assert.Equal("8.4", cut.Find("#bike-weight").GetAttribute("value"));
            Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled"));
        });

        cut.Find("#rider-weight").Change("72.0");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.UpdatedProfiles);
            Assert.Equal(new UpdateProfileRequest(72.0, 8.4), api.UpdatedProfiles[0].Request);
            Assert.Contains("Profile saved.", cut.Find("[data-testid=profile-status]").TextContent, StringComparison.Ordinal);
            Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled"));
        });
    }

    [Fact]
    public void Profile_enforces_weight_boundaries_before_submit()
    {
        api.OnGetProfileAsync = _ => Task.FromResult<ProfileResponse?>(new ProfileResponse(75, 10));
        api.OnUpdateProfileAsync = (request, _) => Task.FromResult(new ProfileResponse(request.RiderWeightKg, request.BikeAndEquipmentWeightKg));

        var cut = Render<Profile>();

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("#rider-weight").Change("29.9");
        cut.Find("#bike-weight").Change("8.0");
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("#rider-weight").Change("30");
        cut.Find("#bike-weight").Change("3");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("#rider-weight").Change("250");
        cut.Find("#bike-weight").Change("60");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("#rider-weight").Change("250.1");
        cut.Find("#bike-weight").Change("60.1");
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));
    }

    [Fact]
    public void Profile_shows_server_field_errors()
    {
        api.OnGetProfileAsync = _ => Task.FromResult<ProfileResponse?>(new ProfileResponse(75, 10));
        api.OnUpdateProfileAsync = (_, _) => Task.FromException<ProfileResponse>(
            new ApiProblemException(
                HttpStatusCode.BadRequest,
                "invalid-profile",
                "Profile is invalid.",
                "Review the highlighted profile fields.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["riderWeightKg"] = ["Rider weight must be between 30 and 250 kg."]
                }));

        var cut = Render<Profile>();

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));
        cut.Find("#rider-weight").Change("76");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid=profile-problem]");
            Assert.Contains("invalid-profile", alert.TextContent, StringComparison.Ordinal);
            Assert.Contains("Rider weight must be between 30 and 250 kg.", alert.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Profile_shows_network_problem_when_save_fails()
    {
        api.OnGetProfileAsync = _ => Task.FromResult<ProfileResponse?>(new ProfileResponse(75, 10));
        api.OnUpdateProfileAsync = (_, _) => Task.FromException<ProfileResponse>(new HttpRequestException("offline"));

        var cut = Render<Profile>();

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));
        cut.Find("#bike-weight").Change("10.5");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid=profile-problem]");
            Assert.Contains("We could not save your profile.", alert.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Profile_cancels_in_flight_save_when_disposed()
    {
        CancellationToken observed = default;
        var saveCompletion = new TaskCompletionSource<ProfileResponse>();

        api.OnGetProfileAsync = _ => Task.FromResult<ProfileResponse?>(new ProfileResponse(75, 10));
        api.OnUpdateProfileAsync = (_, ct) =>
        {
            observed = ct;
            ct.Register(() => saveCompletion.TrySetCanceled(ct));
            return saveCompletion.Task;
        };

        var cut = Render<Profile>();

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));
        cut.Find("#bike-weight").Change("10.5");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=profile-save]").HasAttribute("disabled")));

        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Single(api.UpdatedProfiles));
        ((IDisposable)cut.Instance).Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await saveCompletion.Task.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.True(observed.IsCancellationRequested);
    }
}
