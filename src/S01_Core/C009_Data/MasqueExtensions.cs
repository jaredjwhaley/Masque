using System.Data.Common;
using Masque.Core;
using Masque.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers Masque with dependency injection.</summary>
    public static class MasqueServiceCollectionExtensions
    {
        /// <summary>
        /// Registers one <see cref="MasqueGate"/> for the application, built from <see cref="MasqueOptions"/> the
        /// first time it is asked for. Inject the gate and wrap each connection with it.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Sets the options, such as <c>o =&gt; o.Dialects.Add(new SqliteDialect())</c>.</param>
        /// <returns>The same collection, for chaining.</returns>
        public static IServiceCollection AddMasque(this IServiceCollection services, Action<MasqueOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.AddOptions<MasqueOptions>().Configure(configure);
            services.TryAddSingleton(provider => MasqueGate.Create(provider.GetRequiredService<IOptions<MasqueOptions>>().Value));
            return services;
        }
    }
}

namespace Masque.Data
{
    /// <summary>Wraps connections with Masque.</summary>
    public static class DbConnectionMasqueExtensions
    {
        /// <summary>Wraps a connection so its commands are checked and its results masked; same as <see cref="MasqueGate.Wrap"/>.</summary>
        /// <param name="connection">The raw connection; the wrapper owns it from now on.</param>
        /// <param name="gate">The application's gate.</param>
        /// <param name="allowRaw">Raw types to allow on this connection; narrows the policy, never widens it.</param>
        /// <returns>The wrapped connection.</returns>
        public static MaskingDbConnection WithMasque(this DbConnection connection, MasqueGate gate, IEnumerable<SensitiveDataType>? allowRaw = null)
        {
            ArgumentNullException.ThrowIfNull(gate);
            return gate.Wrap(connection, allowRaw);
        }
    }
}
