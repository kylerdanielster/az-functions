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

            await Task.Delay(TimeSpan.FromSeconds(2));

            await client.RaiseEventAsync(instanceId, SftpOrchestration.PersonReceivedEvent, person);
            logger.LogInformation("[SFTP] Data feed orchestration {id} — person event raised.", instanceId);

            await client.RaiseEventAsync(instanceId, SftpOrchestration.AddressReceivedEvent, address);
            logger.LogInformation("[SFTP] Data feed orchestration {id} — address event raised.", instanceId);

            for (int i = 1; i <= 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                var metadata = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true);

                if (metadata is null)
                {
                    logger.LogError("[SFTP] Data feed orchestration {id} — instance not found.", instanceId);
                    return;
                }

                logger.LogInformation("[SFTP] Data feed orchestration {id} — poll {attempt}: {status}.",
                    instanceId, i, metadata.RuntimeStatus);

                if (metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
                {
                    logger.LogInformation("[SFTP] Data feed orchestration {id} — complete!", instanceId);
                    return;
                }

                if (metadata.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
                {
                    logger.LogError("[SFTP] Data feed orchestration {id} — failed.", instanceId);
                    return;
                }
            }

            logger.LogError("[SFTP] Data feed orchestration {id} — timed out after 30 poll attempts.", instanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Data feed failed.");
        }
    }
}
