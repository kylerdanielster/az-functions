using System.Text.Json;
using Bogus;

var type = args.Length > 0 ? args[0] : "both";

var personFaker = new Faker<PersonData>()
    .CustomInstantiator(f => new PersonData(
        f.Name.FirstName(),
        f.Name.LastName(),
        f.Date.Past(50, DateTime.Now.AddYears(-18)).ToString("yyyy-MM-dd")));

var addressFaker = new Faker<AddressData>()
    .CustomInstantiator(f => new AddressData(
        f.Address.StreetAddress(),
        f.Address.City(),
        f.Address.StateAbbr(),
        f.Address.ZipCode("#####")));

var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

if (type == "person")
    Console.WriteLine(JsonSerializer.Serialize(personFaker.Generate(), options));
else if (type == "address")
    Console.WriteLine(JsonSerializer.Serialize(addressFaker.Generate(), options));
else
{
    Console.WriteLine(JsonSerializer.Serialize(personFaker.Generate(), options));
    Console.WriteLine(JsonSerializer.Serialize(addressFaker.Generate(), options));
}

record PersonData(string FirstName, string LastName, string DateOfBirth);
record AddressData(string Street, string City, string State, string ZipCode);
