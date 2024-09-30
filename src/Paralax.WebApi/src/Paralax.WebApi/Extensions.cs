using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetJSON; 
using Paralax.WebApi.Exceptions;
using Paralax.WebApi.Formatters;
using Paralax.WebApi.Requests;
using Open.Serialization.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Paralax.Core;

namespace Paralax.WebApi
{
    public static class Extensions
    {
        private static readonly byte[] InvalidJsonRequestBytes = Encoding.UTF8.GetBytes("An invalid JSON was sent.");
        private const string JsonContentType = "application/json";
        private const string LocationHeader = "Location";
        private const string EmptyJsonObject = "{}";
        private const string SectionName = "webApi";
        private const string RegistryName = "webApi";
        private static bool _bindRequestFromRoute;

        public static IApplicationBuilder UseEndpoints(this IApplicationBuilder app, Action<IEndpointsBuilder> build, bool useAuthorization = true, Action<IApplicationBuilder> middleware = null)
        {
            var definitions = app.ApplicationServices.GetRequiredService<WebApiEndpointDefinitions>();
            app.UseRouting();
            if (useAuthorization)
            {
                app.UseAuthorization();
            }

            middleware?.Invoke(app);
            app.UseEndpoints(router => build(new EndpointsBuilder(router, definitions)));

            return app;
        }

        public static IApplicationBuilder UseErrorHandler(this IApplicationBuilder app)
            => app.UseMiddleware<ErrorHandlerMiddleware>();

        public static Task<TResult> DispatchAsync<TRequest, TResult>(this HttpContext context, TRequest request)
            where TRequest : class, IRequest
        {
            var handler = context.RequestServices.GetRequiredService<IRequestHandler<TRequest, TResult>>();
            return handler.HandleAsync(request);
        }

         public static IParalaxBuilder AddWebApi(this IParalaxBuilder builder, Action<IMvcCoreBuilder> configureMvc = null,
         IJsonSerializer jsonSerializer = null,  string sectionName = SectionName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
            {
                sectionName = SectionName;
            }

            if (!builder.TryRegister(RegistryName))
            {
                return builder;
            }

            if (jsonSerializer is null)
        {
            var jsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                Converters = {new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)}
            };

            jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            var factory = new Open.Serialization.Json.System.JsonSerializerFactory(jsonSerializerOptions);

            jsonSerializer = factory.GetSerializer();
        }

        if (jsonSerializer.GetType().Namespace?.Contains("Newtonsoft") == true)
        {
            builder.Services.Configure<KestrelServerOptions>(o => o.AllowSynchronousIO = true);
            builder.Services.Configure<IISServerOptions>(o => o.AllowSynchronousIO = true);
        }

        builder.Services.AddSingleton(jsonSerializer);
        builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.AddSingleton(new WebApiEndpointDefinitions());
        var options = builder.GetOptions<WebApiOptions>(sectionName);
        builder.Services.AddSingleton(options);
        _bindRequestFromRoute = options.BindRequestFromRoute;

        var mvcCoreBuilder = builder.Services
            .AddLogging()
            .AddMvcCore();

        mvcCoreBuilder.AddMvcOptions(o =>
            {
                o.OutputFormatters.Clear();
                o.OutputFormatters.Add(new JsonOutputFormatter(jsonSerializer));
                o.InputFormatters.Clear();
                o.InputFormatters.Add(new JsonInputFormatter(jsonSerializer));
            })
            .AddDataAnnotations()
            .AddApiExplorer()
            .AddAuthorization();

        configureMvc?.Invoke(mvcCoreBuilder);

        builder.Services.Scan(s =>
            s.FromAssemblies(AppDomain.CurrentDomain.GetAssemblies())
                .AddClasses(c => c.AssignableTo(typeof(IRequestHandler<,>))
                    .WithoutAttribute(typeof(DecoratorAttribute)))
                .AsImplementedInterfaces()
                .WithTransientLifetime());

        builder.Services.AddTransient<IRequestDispatcher, RequestDispatcher>();

        if (builder.Services.All(s => s.ServiceType != typeof(IExceptionToResponseMapper)))
        {
            builder.Services.AddTransient<IExceptionToResponseMapper, EmptyExceptionToResponseMapper>();
        }

        return builder;
    }

        public static async Task<T> ReadJsonAsync<T>(this HttpContext context)
        {
            var logger = context.RequestServices.GetService<ILogger>();
            if (context.Request.Body == null)
            {
                logger?.LogError("Request body is null.");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await context.Response.Body.WriteAsync(InvalidJsonRequestBytes, 0, InvalidJsonRequestBytes.Length);
                return default;
            }

            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var json = await reader.ReadToEndAsync();
                var payload = NetJSON.NetJSON.Deserialize<T>(json);

                if (_bindRequestFromRoute && HasRouteData(context.Request))
                {
                    var values = context.GetRouteData().Values;
                    foreach (var (key, value) in values)
                    {
                        var field = typeof(T).GetField($"<{key}>", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (field != null)
                        {
                            var fieldValue = TypeDescriptor.GetConverter(field.FieldType).ConvertFromInvariantString(value.ToString());
                            field.SetValue(payload, fieldValue);
                        }
                    }
                }
                return payload;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to deserialize JSON.");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await context.Response.Body.WriteAsync(InvalidJsonRequestBytes, 0, InvalidJsonRequestBytes.Length);
                return default;
            }
        }

        public static IParalaxBuilder AddErrorHandler<T>(this IParalaxBuilder builder)
            where T : class, IExceptionToResponseMapper
        {
            builder.Services.AddTransient<ErrorHandlerMiddleware>();
            builder.Services.AddSingleton<IExceptionToResponseMapper, T>();

            return builder;
        }


         public static T ReadQuery<T>(this HttpContext context) where T : class
    {
        var request = context.Request;
        RouteValueDictionary values = null;

        // Parse route data if available
        if (HasRouteData(request))
        {
            values = request.HttpContext.GetRouteData().Values;
        }

        // Parse query string if available
        if (HasQueryString(request))
        {
            var queryString = HttpUtility.ParseQueryString(request.HttpContext.Request.QueryString.Value);
            values ??= new RouteValueDictionary();

            // Add query string parameters to values
            foreach (var key in queryString.AllKeys)
            {
                values.TryAdd(key, queryString[key]);
            }
        }

        // Get JSON serializer from the request services
        var serializer = context.RequestServices.GetRequiredService<IJsonSerializer>();

        // If no values were found, return an empty object
        if (values == null || !values.Any())
        {
            return serializer.Deserialize<T>(EmptyJsonObject);
        }

        // Serialize the dictionary of values to JSON
        var serializedValues = serializer.Serialize(values.ToDictionary(k => k.Key, k => k.Value))
            ?.Replace("\\\"", "\"")  // Unescape quotes
            .Replace("\"{", "{")    // Fix escaped curly brackets
            .Replace("}\"", "}")
            .Replace("\"[", "[")    // Fix escaped square brackets
            .Replace("]\"", "]");

        // Deserialize and return the resulting object
        return serializer.Deserialize<T>(serializedValues);
    }





        public static Task Ok(this HttpResponse response, object data = null)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            return data != null ? response.WriteJsonAsync(data) : Task.CompletedTask;
        }

        public static Task Created(this HttpResponse response, string location = null, object data = null)
        {
            response.StatusCode = (int)HttpStatusCode.Created;
            if (!string.IsNullOrWhiteSpace(location) && !response.Headers.ContainsKey(LocationHeader))
            {
                response.Headers.Add(LocationHeader, location);
            }
            return data != null ? response.WriteJsonAsync(data) : Task.CompletedTask;
        }

        public static Task Accepted(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Accepted;
            return Task.CompletedTask;
        }

        public static Task NoContent(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return Task.CompletedTask;
        }

        public static Task BadRequest(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return Task.CompletedTask;
        }

        public static Task Unauthorized(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }

        public static Task Forbidden(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }

        public static Task NotFound(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return Task.CompletedTask;
        }

        public static Task InternalServerError(this HttpResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            return Task.CompletedTask;
        }

        public static async Task WriteJsonAsync<T>(this HttpResponse response, T value)
        {
            response.ContentType = JsonContentType;
            var json = NetJSON.NetJSON.Serialize(value);
            await response.WriteAsync(json);
        }

        public static T Bind<T>(this T model, Expression<Func<T, object>> expression, object value)
            => model.Bind<T, object>(expression, value);

        private static TModel Bind<TModel, TProperty>(this TModel model, Expression<Func<TModel, TProperty>> expression, object value)
        {
            if (!(expression.Body is MemberExpression memberExpression))
            {
                memberExpression = ((UnaryExpression)expression.Body).Operand as MemberExpression;
            }

            if (memberExpression == null)
            {
                throw new InvalidOperationException("Invalid member expression.");
            }

            var propertyName = memberExpression.Member.Name.ToLowerInvariant();
            var modelType = model.GetType();
            var field = modelType.GetField($"<{propertyName}>", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                return model;
            }

            field.SetValue(model, value);
            return model;
        }

        private static bool HasQueryString(this HttpRequest request)
            => request.Query.Any();

        private static bool HasRouteData(this HttpRequest request) => request.HttpContext.GetRouteData().Values.Any();

        private class EmptyExceptionToResponseMapper : IExceptionToResponseMapper
        {
            public ExceptionResponse Map(Exception exception) => null;
        }
    }
}
