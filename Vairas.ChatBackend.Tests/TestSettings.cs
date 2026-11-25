using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;

namespace Vairas.ChatBackend.Tests;

public sealed class TestSettings : IChatSettings { public ChatOptions Current { get; } = new(); }
