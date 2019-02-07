using GrainInterfaces;
using Grains;
using Grains.Redux;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Configuration;
using System.Data.SqlClient;
using System.Data.Common;

namespace OrleansHost
{
    internal class Program
    {
        private static readonly ManualResetEvent StopSilo = new ManualResetEvent(false);

        private static async Task Main(string[] args)
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
            string connectionString=null;
            ISiloHost silo;
            try
            {
                // string connectionString = config.GetConnectionString("DataConnectionString");
                silo = new SiloHostBuilder()
                    .UseEnvironment(environment)
                    .ConfigureLogging((context, logging) =>
                    {
                        logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                        logging.AddConsole();
                        logging.AddDebug();
                    })
                    .ConfigureAppConfiguration((context, builder) => {
                        builder
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddInMemoryCollection(new Dictionary<string, string> // add default settings, that will be overridden by commandline
                            {
                                {"Id", "OrleansHost"},
                                {"Version", "1.0.0"},
                                {"ClusterId", "rrod-cluster"},
                                {"ServiceId", "rrod"}
                            })
                            .AddCommandLine(args)
                            .AddJsonFile("OrleansHost.settings.json", optional: true, reloadOnChange: true)
                            .AddJsonFile($"OrleansHost.settings.{environment}.json", optional: true, reloadOnChange: true)
                            .AddJsonFile("/run/config/OrleansHost.settings.json", optional: true, reloadOnChange: true)
                            .AddDockerSecrets("/run/secrets", optional: true)   // we can pas connectionstring as a docker secret
                            .AddUserSecrets<Program>(optional: true)            // for development
                            .AddEnvironmentVariables("RROD_");                  // can override all settings (i.e. URLS) by passing an environment variable
                    })
                    .AddStartupTask<SettingsLogger>()
                    //Испольузем  в качестве кластера SQL Server
                    .UseAdoNetClustering(builder => builder.Configure((AdoNetClusteringSiloOptions options, IConfiguration cfg) =>
                    {
                        if (!CheckDBInit(cfg.GetConnectionString("AdoNetConnectionString")))
                        {
                            InitDB(cfg.GetConnectionString("AdoNetConnectionString"));
                        }
                        options.ConnectionString = cfg.GetConnectionString("AdoNetConnectionString");
                        options.Invariant = cfg.GetValue<string>("SQLProvider");

                    }))
                    //Prev 4 use Azure - Для использоания Azure
                    //.UseAzureStorageClustering(builder => builder.Configure((AzureStorageClusteringOptions options, IConfiguration cfg) => options.ConnectionString = cfg.GetConnectionString("DataConnectionString")))
                    
                    .ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000)
                    .ConfigureServices((context, services) =>
                    {
                        var config = context.Configuration;

                        var dataConnectionString = config.GetConnectionString("DataConnectionString"); //Для Azure
                        var reduxConnectionString = config.GetConnectionString("ReduxConnectionString");
                        var adoNetConnectionString = config.GetConnectionString("AdoNetConnectionString"); //Для SQL

                        services.AddOptions();
                        services.Configure<ClusterOptions>(config);
                       
                       
                    //    services.UseAzureTableReminderService(options => options.ConnectionString = dataConnectionString);
                        services.AddSingleton(new ReduxTableStorage<CertState>(reduxConnectionString));
                        services.AddSingleton(new ReduxTableStorage<UserState>(reduxConnectionString));
                        services.AddSingleton(new ReduxTableStorage<CounterState>(reduxConnectionString));
                        services.AddSingleton(new ReduxTableStorage<StringStoreState>(reduxConnectionString));
                    })
                    .ConfigureApplicationParts(parts =>
                    {
                        parts.AddApplicationPart(typeof(CounterGrain).Assembly).WithReferences();
                        parts.AddApplicationPart(typeof(AzureQueueDataAdapterV2).Assembly).WithReferences();
                    })
                     .AddAdoNetGrainStorageAsDefault(builder => builder.Configure((AdoNetGrainStorageOptions options, IConfiguration cfg) =>
                     {
                         connectionString = cfg.GetConnectionString("AdoNetConnectionString");
                         options.ConnectionString = cfg.GetConnectionString("AdoNetConnectionString");
                         options.Invariant = cfg.GetValue<string>("SQLProvider"); //Провайдер

                     }))
                    .AddAdoNetGrainStorage("PubSubStore", builder => builder.Configure((AdoNetGrainStorageOptions options, IConfiguration cfg) =>
                    {
                        options.ConnectionString = cfg.GetConnectionString("AdoNetConnectionString");
                        options.Invariant = cfg.GetValue<string>("SQLProvider"); //Провайдер


                    }))
                    .UseAdoNetReminderService(builder => builder.Configure((AdoNetReminderTableOptions options, IConfiguration cfg) =>
                    {
                        options.ConnectionString = cfg.GetConnectionString("AdoNetConnectionString");
                        options.Invariant = cfg.GetValue<string>("SQLProvider");//Провайдер

                    }))
                    .AddSimpleMessageStreamProvider("Default", builder => builder.Configure((SimpleMessageStreamProviderOptions  options, IConfiguration cfg) =>
                    {
                        options.FireAndForgetDelivery = true;
                        options.OptimizeForImmutableData = true;
                        options.PubSubType = Orleans.Streams.StreamPubSubType.ExplicitGrainBasedAndImplicit;
                     //   cfg.GetSection("AdoNetConnectionString").Bind(options);
                    }))


                    //.AddRe
                    //Меняем на Redis
                    //  .AddAzureTableGrainStorageAsDefault(builder => builder.Configure((AzureTableStorageOptions options, IConfiguration cfg) => options.ConnectionString = cfg.GetConnectionString("DataConnectionString")))
                    // .AddAzureTableGrainStorage("PubSubStore", builder => builder.Configure((AzureTableStorageOptions options, IConfiguration cfg) => options.ConnectionString = cfg.GetConnectionString("DataConnectionString")))
                    // .AddAzureQueueStreams<AzureQueueDataAdapterV2>("Default", builder => builder.Configure((AzureQueueOptions options, IConfiguration cfg) => options.ConnectionString = cfg.GetConnectionString("DataConnectionString"))
                    //)
                    .Build();
            }
            catch (Exception e)
            {
                Console.WriteLine("Error building silo host: " + e.Message);
                throw;
            }

            // If our process is stopped, close the silo nicely so active grains get deactivated
            AssemblyLoadContext.Default.Unloading += context =>
            {
                StopSilo.Set();
            };

            // Make Ctrl-C stop our process
            Console.CancelKeyPress += (sender, e) =>
            {
                Environment.Exit(0);
            };

            try
            {
                Console.WriteLine("Silo starting...");
                await silo.StartAsync();
                Console.WriteLine("Silo started");

                StopSilo.WaitOne();

                Console.WriteLine("Silo stopping...");
                await silo.StopAsync();
                Console.WriteLine("Silo Stopped");
            }
            catch (SqlException e)
            {
                //Инициализация БД
            


            }
            catch (OrleansLifecycleCanceledException e)
            {
                if (e.InnerException is SqlException && ((SqlException)e.InnerException).Number == 208) //Error DB Not init
                {
                    Console.WriteLine("DataBase Not initialised...");
                }
                Console.WriteLine("Silo could not be started with exception: " + e.InnerException.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Silo could not be started with exception: " + e.Message);
            }
        }

        static bool CheckDBInit(string connectionString)
        {
            SqlClientFactory newFactory = SqlClientFactory.Instance;

            SqlConnection sqlConnection = new SqlConnection(connectionString);
            sqlConnection.Open();
            DbCommand cmd = newFactory.CreateCommand();
            //MSSQL, Pstrgre и MySQL
            cmd.CommandText = "select case when exists((select* from information_schema.tables where table_name = 'OrleansQuery')) then 1 else 0 end";
            cmd.Connection = sqlConnection;
            var res = (int)cmd.ExecuteScalar();
            return res == 1;

        }
        static bool InitDB(string connectionString)
        {
            SqlClientFactory newFactory = SqlClientFactory.Instance;

            SqlConnection sqlConnection = new SqlConnection(connectionString);
            sqlConnection.Open();
            if (!Directory.Exists("OrleansAdoNetContent\\SQLServer"))
            {
                return false;
            };
            //xxx-Main should be the first :)
            foreach (var scriptFile in Directory.GetFiles("OrleansAdoNetContent\\SQLServer", "*.sql").OrderByDescending(c=>c.Contains("Main")))  
            {
                DbCommand cmd = newFactory.CreateCommand();
                cmd.CommandText = File.ReadAllText(scriptFile);
                cmd.Connection = sqlConnection;
                cmd.ExecuteNonQuery();
            }
            sqlConnection.Close();

            return true;
        }
    }

   

    class SettingsLogger : IStartupTask
    {
        readonly ILogger logger;
        readonly IConfigurationRoot config;

        public SettingsLogger(IConfiguration config, ILogger<SettingsLogger> logger)
        {
            this.config = config as IConfigurationRoot;
            this.logger = logger;
        }
        public Task Execute(CancellationToken cancellationToken)
        {
            foreach (var provider in config.Providers)
            {
                logger.LogInformation($"Config Provider {provider.GetType().Name}: {provider.GetChildKeys(Enumerable.Empty<string>(), null).Count()} settings");
            }
            return Task.CompletedTask;
        }
    }
}