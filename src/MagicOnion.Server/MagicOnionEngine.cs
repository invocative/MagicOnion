using Grpc.Core;
using MagicOnion.Server.Hubs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace MagicOnion.Server
{
    public static class MagicOnionEngine
    {
        /// <summary>
        /// Search MagicOnion service from all assemblies.
        /// </summary>
        /// <param name="serviceProvider">The service provider is used to resolve dependencies</param>
        /// <param name="isReturnExceptionStackTraceInErrorDetail">If true, when method body throws exception send to client exception.ToString message. It is useful for debugging.</param>
        /// <returns></returns>
        public static MagicOnionServiceDefinition BuildServerServiceDefinition(IServiceProvider serviceProvider, bool isReturnExceptionStackTraceInErrorDetail = false)
        {
            return BuildServerServiceDefinition(serviceProvider, new MagicOnionOptions() { IsReturnExceptionStackTraceInErrorDetail = isReturnExceptionStackTraceInErrorDetail });
        }

        /// <summary>
        /// Search MagicOnion service from all assemblies.
        /// </summary>
        /// <param name="serviceProvider">The service provider is used to resolve dependencies</param>
        /// <param name="options">The options for MagicOnion server</param>
        public static MagicOnionServiceDefinition BuildServerServiceDefinition(IServiceProvider serviceProvider, MagicOnionOptions options)
        {
            return BuildServerServiceDefinition(serviceProvider, AppDomain.CurrentDomain.GetAssemblies(), options);
        }

        /// <summary>
        /// Search MagicOnion service from target assemblies. ex: new[]{ typeof(Startup).GetTypeInfo().Assembly }
        /// </summary>
        /// <param name="serviceProvider">The service provider is used to resolve dependencies</param>
        /// <param name="searchAssemblies">The assemblies to be search for services</param>
        /// <param name="options">The options for MagicOnion server</param>
        public static MagicOnionServiceDefinition BuildServerServiceDefinition(IServiceProvider serviceProvider, Assembly[] searchAssemblies, MagicOnionOptions options)
        {
            var types = searchAssemblies
              .SelectMany(x =>
              {
                  try
                  {
                      return x.GetTypes();
                  }
                  catch (ReflectionTypeLoadException ex)
                  {
                      return ex.Types.Where(t => t != null);
                  }
              });

#pragma warning disable CS8620 // Argument of type cannot be used for parameter of type in due to differences in the nullability of reference types.
            return BuildServerServiceDefinition(serviceProvider, types, options);
#pragma warning restore CS8620 // Argument of type cannot be used for parameter of type in due to differences in the nullability of reference types.
        }

        /// <summary>
        /// Search MagicOnion service from target types.
        /// </summary>
        /// <param name="serviceProvider">The service provider is used to resolve dependencies</param>
        /// <param name="targetTypes">The types to be search for services</param>
        /// <param name="options">The options for MagicOnion server</param>
        public static MagicOnionServiceDefinition BuildServerServiceDefinition(IServiceProvider serviceProvider, IEnumerable<Type> targetTypes, MagicOnionOptions options)
        {
            var handlers = new HashSet<MethodHandler>();
            var streamingHubHandlers = new List<StreamingHubHandler>();

            var types = targetTypes
              .Where(x => typeof(IServiceMarker).IsAssignableFrom(x))
              .Where(x => !x.GetTypeInfo().IsAbstract)
              .Where(x => x.GetCustomAttribute<IgnoreAttribute>(false) == null)
              .ToArray();

            var logger = serviceProvider.GetRequiredService<IMagicOnionLogger>();
            logger.BeginBuildServiceDefinition();
            var sw = Stopwatch.StartNew();

            try
            {
                foreach (var classType in types)
                {
                    var className = classType.Name;
                    if (!classType.GetConstructors().Any(x => x.GetParameters().Length == 0))
                    {
                        // supports paramaterless constructor after v2.1(DI support).
                        // throw new InvalidOperationException(string.Format("Type needs parameterless constructor, class:{0}", classType.FullName));
                    }

                    var isStreamingHub = typeof(IStreamingHubMarker).IsAssignableFrom(classType);
                    HashSet<StreamingHubHandler>? tempStreamingHubHandlers = null;
                    if (isStreamingHub)
                    {
                        tempStreamingHubHandlers = new HashSet<StreamingHubHandler>();
                    }

                    var inheritInterfaces = classType.GetInterfaces()
                        .Select(z => classType.GetInterfaceMap(z));
                    foreach (var @interface in inheritInterfaces)
                    {
                        if(!@interface.InterfaceType.IsAssignableFrom(classType))
                            throw new NotImplementedException($"Type '{classType.FullName}' has no implementation of interface '{@interface.InterfaceType.FullName}'.");
                    }

                    var interfaceMap = inheritInterfaces.Select(x => classType.GetInterfaceMap(x.InterfaceType)).ToArray();

                    foreach (var mapping in interfaceMap)
                        CollectDataFromMap(mapping);


                    void CollectDataFromMap(InterfaceMapping map)
                    {
                        for (int i = 0; i <  map.TargetMethods.Length; ++i)
                        {
                            var methodInfo = map.TargetMethods[i];
                            var methodName = map.InterfaceMethods[i].Name;

                            if (methodInfo.IsSpecialName && (methodInfo.Name.StartsWith("set_") || methodInfo.Name.StartsWith("get_"))) continue;
                            if (methodInfo.GetCustomAttribute<IgnoreAttribute>(false) != null) continue; // ignore

                            // ignore default methods
                            if (methodName == "Equals"
                                    || methodName == "GetHashCode"
                                    || methodName == "GetType"
                                    || methodName == "ToString"
                                    || methodName == "WithOptions"
                                    || methodName == "WithHeaders"
                                    || methodName == "WithDeadline"
                                    || methodName == "WithCancellationToken"
                                    || methodName == "WithHost"
                                    )
                            {
                                continue;
                            }

                            // register for StreamingHub
                            if (isStreamingHub && methodName != "Connect")
                            {
                                var streamingHandler = new StreamingHubHandler(classType, methodInfo, new StreamingHubHandlerOptions(options), serviceProvider);
                                if (!tempStreamingHubHandlers!.Add(streamingHandler))
                                {
                                    throw new InvalidOperationException($"Method does not allow overload, {className}.{methodName}");
                                }
                                continue;
                            }
                            else
                            {
                                // create handler
                                var handler = new MethodHandler(classType, methodInfo, methodName, new MethodHandlerOptions(options), serviceProvider);
                                if (!handlers.Add(handler))
                                {
                                    throw new InvalidOperationException($"Method does not allow overload, {className}.{methodName}");
                                }
                            }
                        }
                    }


                        

                    if (isStreamingHub)
                    {
                        var connectHandler = new MethodHandler(classType, classType.GetMethod("Connect")!, "Connect", new MethodHandlerOptions(options), serviceProvider);
                        if (!handlers.Add(connectHandler))
                        {
                            throw new InvalidOperationException($"Method does not allow overload, {className}.Connect");
                        }

                        streamingHubHandlers.AddRange(tempStreamingHubHandlers!);
                        StreamingHubHandlerRepository.RegisterHandler(connectHandler, tempStreamingHubHandlers.ToArray());
                        IGroupRepositoryFactory factory;
                        var attr = classType.GetCustomAttribute<GroupConfigurationAttribute>(true);
                        if (attr != null)
                        {
                            factory = attr.Create();
                        }
                        else
                        {
                            factory = serviceProvider.GetRequiredService<IGroupRepositoryFactory>();
                        }
                        StreamingHubHandlerRepository.AddGroupRepository(connectHandler, factory.CreateRepository(options.SerializerOptions, logger));
                    }
                }
            }
            catch (AggregateException agex)
            {
                ExceptionDispatchInfo.Capture(agex.InnerExceptions[0]).Throw();
            }

            var result = new MagicOnionServiceDefinition(handlers.ToArray(), streamingHubHandlers.ToArray());

            sw.Stop();
            logger.EndBuildServiceDefinition(sw.Elapsed.TotalMilliseconds);

            return result;
        }
    }
}