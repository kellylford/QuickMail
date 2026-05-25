using System.Collections.Generic;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ITemplateService
{
    Task<List<MessageTemplate>> LoadAllAsync();
    Task<MessageTemplate> AddAsync(MessageTemplate template);
    Task UpdateAsync(MessageTemplate template);
    Task DeleteAsync(int id);
}
