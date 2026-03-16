using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function;

public class SftpDataFeed(IHttpClientFactory httpClientFactory)
{
    private static readonly Faker<PersonData> PersonFaker = new Faker<PersonData>()
        .CustomInstantiator(f => new PersonData(
            f.Name.FirstName(),
            f.Name.LastName(),
            f.Date.Past(50, DateTime.Now.AddYears(-18)).ToString("yyyy-MM-dd")));

    private static readonly Faker<AddressData> AddressFaker = new Faker<AddressData>()
        .CustomInstantiator(f => new AddressData(
            f.Address.StreetAddress(),
            f.Address.City(),
            f.Address.StateAbbr(),
            f.Address.ZipCode("#####")));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Function(nameof(RunDataFeed))]
    public async Task RunDataFeed(
        [TimerTrigger("0 0 0 * * *")] TimerInfo timerInfo,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(SftpDataFeed));

        try
        {
            string baseUrl = Environment.GetEnvironmentVariable("ORCHESTRATION_BASE_URL")
                ?? throw new InvalidOperationException("ORCHESTRATION_BASE_URL not configured.");

            var person = PersonFaker.Generate();
            var address = AddressFaker.Generate();

            logger.LogInformation("[SFTP] Data feed starting — person: {first} {last}, address: {street}, {city}.",
                person.FirstName, person.LastName, address.Street, address.City);

            using var httpClient = httpClientFactory.CreateClient();

            // Start the orchestration
            var startResponse = await httpClient.PostAsync($"{baseUrl}/sftp/start", null);
            startResponse.EnsureSuccessStatusCode();

            using var startBody = await JsonDocument.ParseAsync(
                await startResponse.Content.ReadAsStreamAsync());
            string instanceId = startBody.RootElement.TryGetProperty("id", out var idProp)
                ? idProp.GetString() ?? throw new InvalidOperationException("Start response missing 'id'.")
                : startBody.RootElement.GetProperty("Id").GetString()
                    ?? throw new InvalidOperationException("Start response missing 'Id'.");

            logger.LogInformation("[SFTP] Data feed orchestration {id} created.", instanceId);

            // Send person data
            var personResponse = await httpClient.PostAsJsonAsync(
                $"{baseUrl}/sftp/person/{instanceId}", person, JsonOptions);
            personResponse.EnsureSuccessStatusCode();
            logger.LogInformation("[SFTP] Data feed orchestration {id} — person data sent.", instanceId);

            // Send address data
            var addressResponse = await httpClient.PostAsJsonAsync(
                $"{baseUrl}/sftp/address/{instanceId}", address, JsonOptions);
            addressResponse.EnsureSuccessStatusCode();
            logger.LogInformation("[SFTP] Data feed orchestration {id} — address data sent, orchestration will complete asynchronously.", instanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Data feed failed.");
        }
    }

}
