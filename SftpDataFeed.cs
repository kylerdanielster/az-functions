using Bogus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace Company.Function;

public static class SftpDataFeed
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

    [Function(nameof(RunDataFeed))]
    public static async Task RunDataFeed(
        [TimerTrigger("0 0 0 1 1 *", RunOnStartup = true)] TimerInfo timerInfo,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(SftpDataFeed));

        try
        {
            var person = PersonFaker.Generate();
            var address = AddressFaker.Generate();

            logger.LogInformation("[SFTP] Data feed starting — person: {first} {last}, address: {street}, {city}.",
                person.FirstName, person.LastName, address.Street, address.City);

            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(SftpOrchestration));
            logger.LogInformation("[SFTP] Data feed orchestration {id} created.", instanceId);

            await client.RaiseEventAsync(instanceId, SftpOrchestration.PersonReceivedEvent, person);
            logger.LogInformation("[SFTP] Data feed orchestration {id} — person event raised.", instanceId);

            await client.RaiseEventAsync(instanceId, SftpOrchestration.AddressReceivedEvent, address);
            logger.LogInformation("[SFTP] Data feed orchestration {id} — events raised, orchestration will complete asynchronously.", instanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Data feed failed.");
        }
    }
}
