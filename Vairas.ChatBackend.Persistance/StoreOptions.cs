using System.Buffers.Binary;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Persistance;

public sealed class StoreOptions { public Guid DeploymentId { get; set; } }
