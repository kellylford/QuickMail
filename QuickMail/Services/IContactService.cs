using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IContactService
{
    Task UpsertContactAsync(ContactModel contact);
    Task<List<ContactModel>> SearchContactsAsync(string prefix, CancellationToken ct = default);
    Task<List<ContactModel>> LoadAllContactsAsync();
    Task DeleteContactAsync(int id);
}
