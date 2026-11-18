using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseMgmt.Web.Tests;

// Runs in the build: the app starts without any Azure settings and answers /healthz and /.
public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_returns_200()
    {
        var response = await factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Home_page_renders_without_azure_settings()
    {
        var response = await factory.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Case Management - platform test", await response.Content.ReadAsStringAsync());
    }
}
