﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI.Embeddings;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI.TextEmbedding;
using Microsoft.SemanticKernel.Connectors.Memory.AzureCognitiveSearch;
using Microsoft.SemanticKernel.Connectors.Memory.Chroma;
using Microsoft.SemanticKernel.Connectors.Memory.Qdrant;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Skills.Core;
using Microsoft.SemanticKernel.TemplateEngine;
using SemanticKernel.Service.CopilotChat.Extensions;
using SemanticKernel.Service.Options;
using static SemanticKernel.Service.Options.MemoriesStoreOptions;

namespace SemanticKernel.Service;

/// <summary>
/// Extension methods for registering Semantic Kernel related services.
/// </summary>
internal static class SemanticKernelExtensions
{
    /// <summary>
    /// Delegate to register skills with a Semantic Kernel
    /// </summary>
    public delegate Task RegisterSkillsWithKernel(IServiceProvider sp, IKernel kernel);

    /// <summary>
    /// Add Semantic Kernel services
    /// </summary>
    internal static IServiceCollection AddSemanticKernelServices(this IServiceCollection services)
    {
        // Semantic Kernel
        services.AddScoped<IKernel>(sp =>
        {
            IKernel kernel = Kernel.Builder
                .WithLogger(sp.GetRequiredService<ILogger<IKernel>>())
                .WithMemory(sp.GetRequiredService<ISemanticTextMemory>())
                .WithCompletionBackend(sp.GetRequiredService<IOptions<AIServiceOptions>>().Value)
                .WithEmbeddingBackend(sp.GetRequiredService<IOptions<AIServiceOptions>>().Value)
                .Build();

            sp.GetRequiredService<RegisterSkillsWithKernel>()(sp, kernel);
            return kernel;
        });

        // Semantic memory
        services.AddSemanticTextMemory();

        // Register skills
        services.AddScoped<RegisterSkillsWithKernel>(sp => RegisterSkillsAsync);

        return services;
    }

    /// <summary>
    /// Register the skills with the kernel.
    /// </summary>
    private static Task RegisterSkillsAsync(IServiceProvider sp, IKernel kernel)
    {
        // Copilot chat skills
        kernel.RegisterCopilotChatSkills(sp);

        // Time skill
        kernel.ImportSkill(new TimeSkill(), nameof(TimeSkill));

        // Semantic skills
        ServiceOptions options = sp.GetRequiredService<IOptions<ServiceOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(options.SemanticSkillsDirectory))
        {
            foreach (string subDir in Directory.GetDirectories(options.SemanticSkillsDirectory))
            {
                try
                {
                    kernel.ImportSemanticSkillFromDirectory(options.SemanticSkillsDirectory, Path.GetFileName(subDir)!);
                }
                catch (TemplateException e)
                {
                    kernel.Log.LogError("Could not load skill from {Directory}: {Message}", subDir, e.Message);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Add the semantic memory.
    /// </summary>
    private static void AddSemanticTextMemory(this IServiceCollection services)
    {
        MemoriesStoreOptions config = services.BuildServiceProvider().GetRequiredService<IOptions<MemoriesStoreOptions>>().Value;

        switch (config.Type)
        {
            case MemoriesStoreType.Volatile:
                services.AddSingleton<IMemoryStore, VolatileMemoryStore>();
                break;

            case MemoriesStoreType.Qdrant:
                if (config.Qdrant == null)
                {
                    throw new InvalidOperationException("MemoriesStore type is Qdrant and Qdrant configuration is null.");
                }

                services.AddSingleton<IMemoryStore>(sp =>
                {
                    HttpClient httpClient = new(new HttpClientHandler { CheckCertificateRevocationList = true });
                    if (!string.IsNullOrWhiteSpace(config.Qdrant.Key))
                    {
                        httpClient.DefaultRequestHeaders.Add("api-key", config.Qdrant.Key);
                    }

                    var endPointBuilder = new UriBuilder(config.Qdrant.Host);
                    endPointBuilder.Port = config.Qdrant.Port;

                    return new QdrantMemoryStore(
                        httpClient: httpClient,
                        config.Qdrant.VectorSize,
                        endPointBuilder.ToString(),
                        logger: sp.GetRequiredService<ILogger<IQdrantVectorDbClient>>()
                    );
                });
                break;

            case MemoriesStoreType.AzureCognitiveSearch:
                if (config.AzureCognitiveSearch == null)
                {
                    throw new InvalidOperationException("MemoriesStore type is AzureCognitiveSearch and AzureCognitiveSearch configuration is null.");
                }

                services.AddSingleton<IMemoryStore>(sp =>
                {
                    return new AzureCognitiveSearchMemoryStore(config.AzureCognitiveSearch.Endpoint, config.AzureCognitiveSearch.Key);
                });
                break;
            case MemoriesStoreOptions.MemoriesStoreType.Chroma:
                if (config.Chroma == null)
                {
                    throw new InvalidOperationException("MemoriesStore type is Chroma and Chroma configuration is null.");
                }

                services.AddSingleton<IMemoryStore>(sp =>
                {
                    HttpClient httpClient = new(new HttpClientHandler { CheckCertificateRevocationList = true });
                    var endPointBuilder = new UriBuilder(config.Chroma.Host);
                    endPointBuilder.Port = config.Chroma.Port;

                    return new ChromaMemoryStore(
                        httpClient: httpClient,
                        endpoint: endPointBuilder.ToString(),
                        logger: sp.GetRequiredService<ILogger<IChromaClient>>()
                    );
                });
                break;

            default:
                throw new InvalidOperationException($"Invalid 'MemoriesStore' type '{config.Type}'.");
        }

        services.AddScoped<ISemanticTextMemory>(sp => new SemanticTextMemory(
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<IOptions<AIServiceOptions>>().Value
                .ToTextEmbeddingsService(logger: sp.GetRequiredService<ILogger<AIServiceOptions>>())));
    }

    /// <summary>
    /// Add the completion backend to the kernel config
    /// </summary>
    private static KernelBuilder WithCompletionBackend(this KernelBuilder kernelBuilder, AIServiceOptions options)
    {
        return options.Type switch
        {
            AIServiceOptions.AIServiceType.AzureOpenAI
                => kernelBuilder.WithAzureChatCompletionService(options.Models.Completion, options.Endpoint, options.Key),
            AIServiceOptions.AIServiceType.OpenAI
                => kernelBuilder.WithOpenAIChatCompletionService(options.Models.Completion, options.Key),
            _
                => throw new ArgumentException($"Invalid {nameof(options.Type)} value in '{AIServiceOptions.PropertyName}' settings."),
        };
    }

    /// <summary>
    /// Add the embedding backend to the kernel config
    /// </summary>
    private static KernelBuilder WithEmbeddingBackend(this KernelBuilder kernelBuilder, AIServiceOptions options)
    {
        return options.Type switch
        {
            AIServiceOptions.AIServiceType.AzureOpenAI
                => kernelBuilder.WithAzureTextEmbeddingGenerationService(options.Models.Embedding, options.Endpoint, options.Key),
            AIServiceOptions.AIServiceType.OpenAI
                => kernelBuilder.WithOpenAITextEmbeddingGenerationService(options.Models.Embedding, options.Key),
            _
                => throw new ArgumentException($"Invalid {nameof(options.Type)} value in '{AIServiceOptions.PropertyName}' settings."),
        };
    }

    /// <summary>
    /// Construct IEmbeddingGeneration from <see cref="AIServiceOptions"/>
    /// </summary>
    /// <param name="options">The service configuration</param>
    /// <param name="httpClient">Custom <see cref="HttpClient"/> for HTTP requests.</param>
    /// <param name="logger">Application logger</param>
    private static ITextEmbeddingGeneration ToTextEmbeddingsService(this AIServiceOptions options,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        return options.Type switch
        {
            AIServiceOptions.AIServiceType.AzureOpenAI
                => new AzureTextEmbeddingGeneration(options.Models.Embedding, options.Endpoint, options.Key, httpClient: httpClient, logger: logger),
            AIServiceOptions.AIServiceType.OpenAI
                => new OpenAITextEmbeddingGeneration(options.Models.Embedding, options.Key, httpClient: httpClient, logger: logger),
            _
                => throw new ArgumentException("Invalid AIService value in embeddings backend settings"),
        };
    }
}
