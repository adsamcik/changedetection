using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Shouldly;
using TUnit.Core;
using Assembly = System.Reflection.Assembly;

namespace ChangeDetection.Tests.Components;

/// <summary>
/// End-to-end tests for Blazor component rendering and parameter configuration.
/// 
/// WHY THESE TESTS EXIST:
/// We had a bug where SetupFlow.razor used [SupplyParameterFromQuery] for InitialInput,
/// but Setup.razor tried to pass it as an explicit parameter:
///   <SetupFlow InitialInput="@InitialInput" />
/// 
/// This caused a runtime error:
///   InvalidOperationException: The property 'InitialInput' on component type 
///   'ChangeDetection.Client.Pages.SetupFlow' cannot be set explicitly because 
///   it only accepts cascading values.
/// 
/// These tests catch such issues by:
/// 1. Actually rendering pages via HTTP and checking for 500 errors
/// 2. Scanning component metadata for invalid attribute combinations
/// </summary>
[Category("Integration")]
public class BlazorComponentRenderingTests
{
    private HttpClient _client = null!;
    private BlazorWebApplicationFactory _factory = null!;

    [Before(Test)]
    public void SetUp()
    {
        _factory = new BlazorWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [After(Test)]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// Renders the setup page with a query parameter to verify the component hierarchy works.
    /// This would have caught the [SupplyParameterFromQuery] bug immediately.
    /// </summary>
    [Test]
    public async Task SetupPage_WithQueryParameter_ShouldRenderWithoutError()
    {
        EnsureClientAssemblyAvailable();

        // Arrange - request the setup page with an input query parameter
        // This is the exact scenario that was failing
        var url = "/setup?input=https://example.com";

        // Act
        var response = await _client.GetAsync(url);

        // Assert
        // The page should render successfully (200 OK), not throw a 500 error
        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError,
            "Setup page should render without throwing InvalidOperationException. " +
            "Check that child components use [Parameter] not [SupplyParameterFromQuery] for explicitly passed props.");
        
        // Accept 200 (rendered) or redirect to auth, but not 500
        ((int)response.StatusCode).ShouldBeLessThan(500,
            $"Page returned {response.StatusCode} - server error indicates component misconfiguration");
    }

    /// <summary>
    /// Renders all main pages to catch any component rendering errors.
    /// Note: Some pages may fail due to runtime dependencies (e.g., HttpClient calls during pre-render).
    /// This test focuses on catching component misconfiguration errors like invalid parameter attributes.
    /// </summary>
    [Test]
    [Arguments("/")]
    [Arguments("/setup")]
    [Arguments("/setup?input=test")]
    [Arguments("/setup?input=https://example.com/page")]
    public async Task AllPages_ShouldRenderWithoutServerError(string path)
    {
        EnsureClientAssemblyAvailable();

        // Act
        var response = await _client.GetAsync(path);

        // Assert - no 500 errors
        ((int)response.StatusCode).ShouldBeLessThan(500,
            $"Page '{path}' returned {response.StatusCode} - " +
            "this indicates a component configuration error such as invalid parameter attributes");
    }

    public class BlazorWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Use Testing environment to avoid file logging conflicts
            // Development environment creates file logs that can conflict
            // when multiple test instances run concurrently
            builder.UseEnvironment("Testing");
            
            builder.ConfigureLogging(logging =>
            {
                // Clear all logging providers to avoid file conflicts
                logging.ClearProviders();
            });
        }
    }

    private static void EnsureClientAssemblyAvailable()
    {
        try
        {
            _ = Assembly.Load("ChangeDetection.Client");
        }
        catch (Exception ex)
        {
            Skip.Test(
                "Skipping Blazor client rendering tests because the ChangeDetection.Client assembly is not available. " +
                "If you're running tests without the Blazor WebAssembly workload, run with /p:SkipClientProjectReference=true (these tests will be skipped), " +
                "or install the 'wasm-tools' workload to enable full client build.\n\n" +
                $"Load failure: {ex.GetType().Name}: {ex.Message}");
            return; // Skip.Test throws, but compiler doesn't know that
        }
    }
}

/// <summary>
/// Static analysis tests that scan component metadata to catch parameter misconfigurations
/// before they cause runtime errors.
/// </summary>
public class BlazorComponentParameterTests
{
    private static Assembly LoadClientAssemblyOrSkip()
    {
        try
        {
            return Assembly.Load("ChangeDetection.Client");
        }
        catch (Exception ex)
        {
            Skip.Test(
                "Skipping Blazor client component metadata tests because the ChangeDetection.Client assembly is not available. " +
                "Install the 'wasm-tools' workload (or build the Client project) to enable these checks.\n\n" +
                $"Load failure: {ex.GetType().Name}: {ex.Message}");
            throw; // Skip.Test throws, but compiler doesn't know - this is unreachable but satisfies return type
        }
    }

    /// <summary>
    /// Components that are used as child components (not routed directly) should not
    /// use [SupplyParameterFromQuery] for properties that are passed from parent components.
    /// Such properties should use [Parameter] instead.
    /// </summary>
    [Test]
    public async Task SetupFlow_SessionId_ShouldBeRegularParameter()
    {
        // Arrange
        var clientAssembly = LoadClientAssemblyOrSkip();
        var setupFlowType = clientAssembly.GetType("ChangeDetection.Client.Pages.SetupFlow");
        
        setupFlowType.ShouldNotBeNull("SetupFlow component should exist");

        var sessionIdProperty = setupFlowType.GetProperty("SessionId");
        sessionIdProperty.ShouldNotBeNull("SessionId property should exist on SetupFlow");

        // Act
        var hasParameterAttribute = sessionIdProperty
            .GetCustomAttributes()
            .Any(a => a.GetType().Name == "ParameterAttribute");
        
        var hasSupplyParameterFromQuery = sessionIdProperty
            .GetCustomAttributes()
            .Any(a => a.GetType().Name == "SupplyParameterFromQueryAttribute");
        
        var hasCascadingParameter = sessionIdProperty
            .GetCustomAttributes()
            .Any(a => a.GetType().Name == "CascadingParameterAttribute");

        // Assert
        hasParameterAttribute.ShouldBeTrue(
            "SessionId should have [Parameter] attribute since it's passed from parent Setup.razor");
        
        hasSupplyParameterFromQuery.ShouldBeFalse(
            "SessionId should NOT have [SupplyParameterFromQuery] - the parent component reads the route parameter and passes it down");
        
        hasCascadingParameter.ShouldBeFalse(
            "SessionId should NOT have [CascadingParameter] - it's passed as an explicit parameter from parent");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that child components used within routed pages don't have conflicting
    /// query parameter bindings that would cause runtime errors.
    /// </summary>
    [Test]
    public async Task ChildComponents_ShouldNotUseSupplyParameterFromQuery_ForExplicitlyPassedProperties()
    {
        // This test scans all components in the Client assembly to detect potential issues
        var clientAssembly = LoadClientAssemblyOrSkip();
        var componentTypes = clientAssembly.GetTypes()
            .Where(t => typeof(ComponentBase).IsAssignableFrom(t) && !t.IsAbstract);

        var issues = new List<string>();

        foreach (var componentType in componentTypes)
        {
            var properties = componentType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            
            foreach (var property in properties)
            {
                var hasSupplyFromQuery = property.GetCustomAttributes()
                    .Any(a => a.GetType().Name == "SupplyParameterFromQueryAttribute");

                if (hasSupplyFromQuery)
                {
                    // Check if this component has a @page directive (is routable)
                    var hasRouteAttribute = componentType.GetCustomAttributes()
                        .Any(a => a.GetType().Name == "RouteAttribute");

                    if (!hasRouteAttribute)
                    {
                        issues.Add(
                            $"Component '{componentType.Name}' has property '{property.Name}' with " +
                            $"[SupplyParameterFromQuery] but the component is not routable (@page). " +
                            $"This will cause runtime errors if the property is passed from a parent component. " +
                            $"Use [Parameter] instead.");
                    }
                }
            }
        }

        issues.ShouldBeEmpty(
            "Non-routable components should not use [SupplyParameterFromQuery]. " +
            $"Found issues:\n{string.Join("\n", issues)}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Scans both Client and Server assemblies for common Blazor component misconfigurations.
    /// </summary>
    [Test]
    public async Task AllComponents_ShouldHaveValidParameterConfiguration()
    {
        var assemblies = new[]
        {
            LoadClientAssemblyOrSkip(),
            Assembly.Load("ChangeDetection")
        };

        var issues = new List<string>();

        foreach (var assembly in assemblies)
        {
            var componentTypes = assembly.GetTypes()
                .Where(t => typeof(ComponentBase).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var componentType in componentTypes)
            {
                var properties = componentType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                
                foreach (var property in properties)
                {
                    var attributes = property.GetCustomAttributes().ToList();
                    var attributeNames = attributes.Select(a => a.GetType().Name).ToList();

                    // Check 1: Can't have both [Parameter] and [CascadingParameter]
                    if (attributeNames.Contains("ParameterAttribute") && 
                        attributeNames.Contains("CascadingParameterAttribute"))
                    {
                        issues.Add(
                            $"Component '{componentType.Name}.{property.Name}' has both " +
                            "[Parameter] and [CascadingParameter] - use only one.");
                    }

                    // Check 2: Can't have both [SupplyParameterFromQuery] and [CascadingParameter]
                    if (attributeNames.Contains("SupplyParameterFromQueryAttribute") && 
                        attributeNames.Contains("CascadingParameterAttribute"))
                    {
                        issues.Add(
                            $"Component '{componentType.Name}.{property.Name}' has both " +
                            "[SupplyParameterFromQuery] and [CascadingParameter] - invalid combination.");
                    }

                    // Check 3: [SupplyParameterFromQuery] should only be on routable components
                    if (attributeNames.Contains("SupplyParameterFromQueryAttribute"))
                    {
                        var hasRouteAttribute = componentType.GetCustomAttributes()
                            .Any(a => a.GetType().Name == "RouteAttribute");

                        if (!hasRouteAttribute)
                        {
                            issues.Add(
                                $"Component '{componentType.Name}.{property.Name}' has " +
                                "[SupplyParameterFromQuery] but component has no @page route. " +
                                "This will fail if property is passed from parent.");
                        }
                    }

                    // Check 4: [SupplyParameterFromForm] should only be on routable components or form handlers
                    if (attributeNames.Contains("SupplyParameterFromFormAttribute"))
                    {
                        var hasRouteAttribute = componentType.GetCustomAttributes()
                            .Any(a => a.GetType().Name == "RouteAttribute");

                        if (!hasRouteAttribute)
                        {
                            issues.Add(
                                $"Component '{componentType.Name}.{property.Name}' has " +
                                "[SupplyParameterFromForm] but component has no @page route.");
                        }
                    }
                }
            }
        }

        issues.ShouldBeEmpty(
            $"Found Blazor component parameter configuration issues:\n{string.Join("\n", issues)}");
        await Task.CompletedTask;
    }
}
