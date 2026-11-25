using System.Linq.Expressions;
using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public interface IChatSettings { ChatOptions Current { get; } }
