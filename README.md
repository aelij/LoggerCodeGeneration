# Microsoft.Extensions.Logging Code Generator

## Use interfaces to write logs using `ILogger`

## Features
* Mapping custom parameter types
* Fallback converter to `string`
* Automatically redirects `Exception` types to the exception parameter
* Generated format strings (from all parameters)
* Optimized using `LoggerMessage` (to to 6 arguments)

## Usage

[![NuGet](https://img.shields.io/nuget/v/Arbel.Extensions.Logging.CodeGeneration.svg?style=flat-square)](https://www.nuget.org/packages/Arbel.Extensions.Logging.CodeGeneration) 

```csharp
[LoggerCategory(Name = "Bar")]
interface IFooLogger
{
   [LoggerEvent(Level = EventLevel.Warning)]
   void Log(Foo foo);
}

class Foo
{
   public int A { get; }
}

void ConfigureServices(IServiceCollection services)
{
  services.Configure<LoggerGeneratorOptions>(o =>
    {
        o.FallbackConverter = value => value + string.Empty;
        o.AddConverter((Foo foo) => foo.A);
    });
  services.AddLogger<IFooLogger>();
}

fooSource.Log(new Foo());
```
