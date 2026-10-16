using Phoenix.Web;
using System.Text.Json;
namespace Phoenix.Test;
public static class OrbitWorkspaceTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static void Conflict(Action action){try{action();}catch(PersonalConflict){return;}throw new Exception("Expected invalid category conflict.");}
    private static void WithStore(Action<PersonalStore> action)
    {
        var previous=Environment.GetEnvironmentVariable("PERSONAL_DATABASE_PATH");
        var folder=Path.Combine(Path.GetTempPath(),"orbit-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        try{Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH",Path.Combine(folder,"orbit.db"));action(new PersonalStore());}
        finally{Environment.SetEnvironmentVariable("PERSONAL_DATABASE_PATH",previous);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
    private static PersonalInput Category(string title,string scope,Guid? parent=null)=>new("category",new(2026,10,17),title,scope,null,null,"","",null,new(){ParentId=parent},Guid.NewGuid());
    public static void Categories()=>WithStore(store=>{
        PersonalEntry Create(PersonalInput i)=>store.CreateAsync(i,default).GetAwaiter().GetResult();
        var root=Create(Category("خودرو","finance"));
        var child=Create(Category("خودرو / تعمیرگاه","finance",root.Id));
        Create(Category("ساخت ساز","task"));
        Conflict(()=>Create(Category("wrong scope","task",root.Id)));
        Conflict(()=>Create(Category("missing","finance",Guid.NewGuid())));
        Conflict(()=>Create(Category("خودرو","finance")));
        Conflict(()=>store.UpdateAsync(root.Id,PersonalRules.Input(root) with {Details=root.Details! with {ParentId=child.Id}},root.Revision,default).GetAwaiter().GetResult());
        Conflict(()=>store.DeleteAsync(root.Id,default).GetAwaiter().GetResult());
        Check(new PersonalStore().ListAsync(default).GetAwaiter().GetResult().Single(e=>e.Id==child.Id).Details?.ParentId==root.Id,"Nested category lost after restart.");
    });
    public static void Drafts()=>WithStore(store=>{
        var date=new DateOnly(2026,10,17);
        var input=new PersonalInput("finance",date,"برداشت بانکی","",70000,"expense","برداشت ۷۰۰۰۰۰ ریال","",null,new(){Status="waiting",SourceId="sms:123:456"},Guid.NewGuid());
        var draft=store.CreateAsync(input,default).GetAwaiter().GetResult();
        decimal Expenses(){var report=JsonSerializer.SerializeToElement(PersonalReports.Build(store.ListAsync(default).GetAwaiter().GetResult(),date,date),PersonalRules.Json);return report.GetProperty("totals").GetProperty("expenses").GetDecimal();}
        Check(Expenses()==0,"Unreviewed SMS was included in totals.");
        var reviewed=PersonalRules.Input(draft) with {Category="خودرو / تعمیرگاه",Details=draft.Details! with {Status="active"}};
        var saved=store.UpdateAsync(draft.Id,reviewed,draft.Revision,default).GetAwaiter().GetResult()!;
        Check(saved.Details?.SourceId=="sms:123:456"&&Expenses()==70000,"Reviewed draft lost its source or amount.");
        store.UpdateAsync(saved.Id,PersonalRules.Input(saved) with {Details=saved.Details! with {Status="cancelled"}},saved.Revision,default).GetAwaiter().GetResult();
        Check(Expenses()==0,"Ignored SMS was counted.");
    });
}
