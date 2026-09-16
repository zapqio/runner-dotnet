using TestModule.Shared;
using TestModule.Shared.Extra;
using Zapqio.Runner.Core;

namespace TestModule.Consumer
{
    /// <summary>Wstrzykuje singleton z paczki współdzielonej - jak TestConnect z NexoClient.</summary>
    public class UsesClientMethod : IRunnerMethod
    {
        private readonly SharedClient _client;

        public UsesClientMethod(SharedClient client)
        {
            _client = client;
        }

        public string NameMethod() => "uses-client";

        public Type InData() => null;

        public Type OutData() => null;

        public Task<string> Run(string data) => Task.FromResult(_client.Hello());
    }

    /// <summary>Dziedziczy po klasie z biblioteki, której moduł współdzielony sam nie ładuje.</summary>
    public class DerivedMethod : SharedBaseMethod
    {
        public override string NameMethod() => "derived";

        public override Task<string> Run(string data) => Task.FromResult(ExtraHelper.Value());
    }

    /// <summary>Konstruktor rzuca - metoda ma zniknąć z listy, reszta ma działać.</summary>
    public class ThrowingMethod : IRunnerMethod
    {
        public ThrowingMethod()
        {
            throw new InvalidOperationException("constructor failed on purpose");
        }

        public string NameMethod() => "throwing";

        public Type InData() => null;

        public Type OutData() => null;

        public Task<string> Run(string data) => Task.FromResult(string.Empty);
    }

    /// <summary>Zwykła klasa, nie IRunnerInjection - nikt jej nie rejestruje.</summary>
    public class NotRegistered
    {
    }

    /// <summary>Wymaga zależności, której nie ma w kontenerze - DI nie potrafi jej utworzyć.</summary>
    public class NeedsUnregisteredMethod : IRunnerMethod
    {
        public NeedsUnregisteredMethod(NotRegistered dependency)
        {
            _ = dependency;
        }

        public string NameMethod() => "needs-unregistered";

        public Type InData() => null;

        public Type OutData() => null;

        public Task<string> Run(string data) => Task.FromResult(string.Empty);
    }
}
