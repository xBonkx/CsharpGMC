using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Worker-only issue pipeline. Does not access WPF controls or retry registration.</summary>
internal static class OrderIssueService
{
    internal sealed record Template(string Path, string Title);

    internal sealed record Request(long ObjectId, AccessSite SiteSnapshot,
        IReadOnlyList<AccessCompany> Companies, PeoplePerson Person, DateTime Day,
        string Number, string OriginalNumber, string Contract, string SiteName,
        string Folder, string Revision, IReadOnlyList<Template> Templates, Guid Operation);

    internal sealed record Result(bool Registered, int Count, string? Revision,
        IReadOnlyList<AccessHistory>? History, string? Warning);

    internal static Result Issue(SqlServerPeopleAccess db, Request request)
    {
        var generated = new List<string>();
        var number = request.Number;
        try
        {
            Directory.CreateDirectory(request.Folder);
            var company = request.Companies.FirstOrDefault(c =>
                c.Name.Contains("ГМЦ", StringComparison.OrdinalIgnoreCase)) ?? request.Companies.FirstOrDefault();

            // Validate the captured revision and directory before generating files.
            var latest = db.Load(false);
            if (latest.Revision != request.Revision || db.Site(request.ObjectId) != request.SiteSnapshot ||
                !db.Companies().SequenceEqual(request.Companies))
                throw new System.Data.DBConcurrencyException("Сотрудники, объект или реквизиты изменились. Перезапустите окно.");

            foreach (var choice in request.Templates)
            {
                if (!File.Exists(choice.Path))
                    throw new FileNotFoundException("Не найден Word-шаблон.", choice.Path);
                var name = PeopleRules.SafeFileName("Приказ №" +
                    number.Replace('/', '-') + " " + choice.Title);
                var current = PeopleDocuments.UniquePath(request.Folder, name, ".docx");
                var mapping = PeopleDocuments.OrderMapping(request.SiteSnapshot, request.Person, company,
                    request.Day, number, request.Contract, request.SiteName);
                PeopleDocuments.FillDocx(choice.Path, current, mapping);
                generated.Add(current); // only files successfully published by this operation
                using (var docx = ZipFile.OpenRead(current))
                    if (docx.GetEntry("word/document.xml") is null)
                        throw new InvalidDataException("Word-документ повреждён.");
                number = PeopleRules.NextNumber(number);
            }
        }
        catch
        {
            // SQL registration has not started: clean up only our own published files.
            foreach (var path in generated)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { /* preserve original failure */ }
            }
            throw;
        }

        string revision;
        try
        {
            var snapshot = new OrderSnapshot(request.Person.Id, request.Person.Fio,
                generated, request.OriginalNumber, request.Operation);
            revision = db.RegisterExport(request.ObjectId, "order", request.Folder,
                JsonSerializer.Serialize(snapshot), request.Operation, request.Revision, request.SiteName).Revision;
        }
        catch (Exception ex)
        {
            // From this point on the commit may have succeeded. Never delete or retry.
            bool recorded = false;
            try { recorded = db.WasRegistered(request.ObjectId, request.Operation); }
            catch { /* unknown commit must remain unknown */ }
            if (!recorded)
                return new Result(false, generated.Count, null, null, ex.Message);

            IReadOnlyList<AccessHistory>? history = null;
            try { history = db.History(request.ObjectId, "order"); }
            catch { /* registration is already confirmed */ }
            // Do not rebase the old request onto an unrelated revision.
            return new Result(true, generated.Count, null, history,
                "Регистрация подтверждена повторным чтением. Для следующей выдачи откройте окно заново.");
        }

        try
        {
            return new Result(true, generated.Count, revision, db.History(request.ObjectId, "order"), null);
        }
        catch (Exception ex)
        {
            // A successful commit cannot be invalidated by a failed history refresh.
            return new Result(true, generated.Count, null, null,
                "Не удалось обновить историю. Откройте окно заново: " + ex.Message);
        }
    }
}
