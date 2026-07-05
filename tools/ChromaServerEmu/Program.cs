using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

// Standalone Chroma SDK "online" emulator probe (issue #292 node 5).
//
// Replicates RazerSdkReader's ChromaMutex WITHOUT its "RzSDKService must be running"
// guard, so we can test whether merely holding the Razer "online" mutexes (and the
// ChromaEmulatorMutex) makes the game's RzChromatic64 InitSDK enter emulator mode and
// self-write the 280F4D44 events buffer when the real Chroma SDK services are stopped.
//
// Mutex names + ACL extracted from RazerSdkReader.dll (MutexHelper / ChromaMutex):
//   Global\{08B4F43A-DA51-4120-B388-CE0F8CE6F61A}  SynapseOnlineMutex
//   Global\{B1570C3F-8B14-45B0-BCEB-C57ED1F5C589}  OldSynapseOnlineMutex
//   Global\{D7B0F094-74E7-4055-BE84-F447B722DEB7}  OldSynapseVersionMutex
//   Global\{5606D98C-C0DC-43E1-9A14-D992B52750F7}  ChromaEmulatorMutex
// Connect event pulsed by ChromaMutex ctor:
//   Global\{89811F96-91C2-4C19-8E0A-54469F491550}
// ACL: WorldSid, MutexRights 2031617 / EventWaitHandleRights 2031619.

const string SynapseOnlineMutex = "Global\\{08B4F43A-DA51-4120-B388-CE0F8CE6F61A}";
const string OldSynapseOnlineMutex = "Global\\{B1570C3F-8B14-45B0-BCEB-C57ED1F5C589}";
const string OldSynapseVersionMutex = "Global\\{D7B0F094-74E7-4055-BE84-F447B722DEB7}";
const string ChromaEmulatorMutex = "Global\\{5606D98C-C0DC-43E1-9A14-D992B52750F7}";
const string ConnectEvent = "Global\\{89811F96-91C2-4C19-8E0A-54469F491550}";

const string EventsMapName = "Global\\{280F4D44-6AC8-4B21-9F33-EFD0548D76B4}";
const int EventsMapSize = 3280896;

bool doShm = args.Contains("--shm");
bool doReg = args.Contains("--synapse-reg");

var held = new List<Mutex>();

static MutexSecurity MutexSec()
{
    var s = new MutexSecurity();
    var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
    s.AddAccessRule(new MutexAccessRule(world, (MutexRights)2031617, AccessControlType.Allow));
    return s;
}

static EventWaitHandleSecurity EventSec()
{
    var s = new EventWaitHandleSecurity();
    var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
    s.AddAccessRule(new EventWaitHandleAccessRule(world, (EventWaitHandleRights)2031619, AccessControlType.Allow));
    return s;
}

Mutex CreateOwned(string name)
{
    bool created;
    var m = MutexAcl.Create(initiallyOwned: true, name, out created, MutexSec());
    Console.WriteLine($"  mutex {name}  owned (createdNew={created})");
    return m;
}

Console.WriteLine("ChromaServerEmu - standalone Chroma 'online' mutex holder (#292 node 5)");
Console.WriteLine($"  --shm        : {doShm}  (also create the 280F4D44 events mapping, {EventsMapSize} bytes)");
Console.WriteLine($"  --synapse-reg: {doReg}  (set HKLM SynapseOnline=1)");
Console.WriteLine();

MemoryMappedFile? shm = null;
if (doShm)
{
    try
    {
        shm = MemoryMappedFile.OpenExisting(EventsMapName, MemoryMappedFileRights.ReadWrite);
        Console.WriteLine($"  shm {EventsMapName}  opened EXISTING");
    }
    catch
    {
        shm = MemoryMappedFile.CreateNew(EventsMapName, EventsMapSize, MemoryMappedFileAccess.ReadWrite);
        Console.WriteLine($"  shm {EventsMapName}  CREATED NEW ({EventsMapSize} bytes)");
    }
}

if (doReg)
{
    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = hklm.CreateSubKey("SOFTWARE\\Razer Chroma SDK");
            k!.SetValue("SynapseOnline", 1, RegistryValueKind.DWord);
            Console.WriteLine($"  reg [{view}] SOFTWARE\\Razer Chroma SDK\\SynapseOnline = 1");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  reg [{view}] FAILED: {e.Message} (run elevated for HKLM)");
        }
    }
}

Console.WriteLine("Creating + owning mutexes:");
held.Add(CreateOwned(SynapseOnlineMutex));
held.Add(CreateOwned(OldSynapseOnlineMutex));
held.Add(CreateOwned(OldSynapseVersionMutex));
held.Add(CreateOwned(ChromaEmulatorMutex));

// Best-effort: create the connect event so clients waiting on it can proceed.
try
{
    var evt = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, ConnectEvent, out var ecreated, EventSec());
    Console.WriteLine($"  event {ConnectEvent}  (createdNew={ecreated})");
}
catch (Exception e)
{
    Console.WriteLine($"  event {ConnectEvent}  FAILED: {e.Message}");
}

Console.WriteLine();
Console.WriteLine("HOLDING. Mutexes are owned by this process. Press Ctrl+C to release and exit.");
Console.WriteLine("Now (in another window) start the game/emu and probe 280F4D44 for landed events.");

var done = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
done.Wait();

Console.WriteLine("Releasing mutexes...");
foreach (var m in held)
{
    try { m.ReleaseMutex(); } catch { }
    m.Dispose();
}
shm?.Dispose();
Console.WriteLine("Done.");
