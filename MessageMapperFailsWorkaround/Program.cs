using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NServiceBus;
using NServiceBus.Config;
using NServiceBus.Config.ConfigurationSource;
using NServiceBus.Features;
using NServiceBus.MessageInterfaces;
using NServiceBus.MessageInterfaces.MessageMapper.Reflection;

namespace MessageMapperFailsWorkaround
{
    class Program
    {
        static void Main(string[] args)
        {
            var configuration = new BusConfiguration();
            configuration.DisableFeature<XmlSerialization>();
            configuration.DisableFeature<JsonSerialization>();
            configuration.DisableFeature<BsonSerialization>();

            

            configuration.EnableFeature<NServiceBus.Workarounds.JsonSerialization>();

            configuration.UsePersistence<InMemoryPersistence>();
            configuration.UseSerialization<NServiceBus.Workarounds.JsonSerializer>();

            var bus = Bus.Create(configuration).Start();
            bus.SendLocal<IMyMessage>(m => { });

            Console.ReadLine();
        }
    }

    #region ReproStuff

    public class Handler : IHandleMessages<IMyMessage>
    {
        public void Handle(IMyMessage message)
        {
            Console.WriteLine("Yeah");
        }
    }

    [JsonObject(ItemTypeNameHandling = TypeNameHandling.All)]
    public interface IMyMessage : IMessage, InterfaceWithGenericProperty<IBar> { }

    public interface InterfaceWithGenericProperty
    {
        object Original { get; set; }
    }

    public interface InterfaceWithGenericProperty<T> : InterfaceWithGenericProperty
    {
        [JsonIgnore]
        new T Original { get; set; }
    }

    public interface IBar
    {
        string Yeah { get; set; }
    }

    #endregion

    #region Infrastructure

    public class ConfigAuditOverride : IProvideConfiguration<AuditConfig>
    {
        public AuditConfig GetConfiguration()
        {
            return new AuditConfig { QueueName = "audit" };
        }
    }

    public class ConfigErrorOverride : IProvideConfiguration<MessageForwardingInCaseOfFaultConfig>
    {
        public MessageForwardingInCaseOfFaultConfig GetConfiguration()
        {
            return new MessageForwardingInCaseOfFaultConfig { ErrorQueue = "error" };
        }
    }
    #endregion
}

