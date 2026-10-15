import { DATACENTER } from '../../../catalog-data.js';
import { describeRegistry } from '../../../catalog.js';
function lookup(id){return DATACENTER.find(e=>e.id===String(id||'').toUpperCase());}
export const HOST_TOOLS=['sudo','ipmitool','busctl','dmidecode','lscpu','lspci','turbostat','numactl','numastat','taskset','chrt','cpupower','x86_energy_perf_policy','nvme','smartctl','storcli','multipath','ethtool','ss','ibstat','ibv_devinfo','perfquery','rdma','perf','runqlat','biosnoop','tcpretrans','offcputime','slabtop','tpm2_pcrread','tpm2_getcap','tpm2_quote','tpm2_checkquote','auditctl','aureport','ausearch','cryptsetup','systemctl','dmesg','journalctl','virsh','kubectl','lsblk','findmnt','swapon','ip','systemd-detect-virt','timeout','tee'];
const descriptions={
  "sudo": "Request host privilege elevation under an operating-system policy; unavailable here.",
  "ipmitool": "Inspect or control BMC sensors, event logs, chassis power and serial-over-LAN.",
  "busctl": "Inspect D-Bus objects and properties on the selected system or BMC.",
  "dmidecode": "Decode firmware-reported system, board and memory inventory.",
  "lscpu": "Inspect CPU architecture, topology and supported information fields.",
  "lspci": "Inspect PCI devices, negotiated links and available error capabilities.",
  "turbostat": "Sample supported CPU frequency, temperature, idle-state and power counters.",
  "numactl": "Inspect NUMA topology or launch a process with a selected NUMA policy.",
  "numastat": "Read NUMA allocation counters or per-node memory information.",
  "taskset": "Inspect or change a process or thread CPU affinity.",
  "chrt": "Inspect or change a process scheduling policy and priority.",
  "cpupower": "Inspect or change supported CPU frequency and power policies.",
  "x86_energy_perf_policy": "Inspect or change supported Intel energy-performance policy fields.",
  "nvme": "Inspect NVMe inventory, logs and controller information; other subcommands can destroy data.",
  "smartctl": "Inspect drive SMART information or initiate a supported self-test.",
  "storcli": "Inspect or administer supported RAID controllers and virtual-drive policies.",
  "multipath": "Inspect device-mapper multipath maps or request a map reload.",
  "ethtool": "Inspect or change Ethernet link, ring, offload and coalescing settings.",
  "ss": "Inspect host sockets and available TCP state information.",
  "ibstat": "Inspect available InfiniBand adapter and port status.",
  "ibv_devinfo": "Inspect RDMA verbs device capabilities and port information.",
  "perfquery": "Read applicable InfiniBand performance-management counters.",
  "rdma": "Inspect RDMA links, resources and device state.",
  "perf": "Collect or inspect kernel performance-counter samples and recordings.",
  "runqlat": "Trace scheduler run-queue latency distributions with BCC.",
  "biosnoop": "Trace individual block-I/O events and optional queue-time information.",
  "tcpretrans": "Trace or count TCP retransmission events with BCC.",
  "offcputime": "Collect stack traces for time spent off CPU.",
  "slabtop": "Inspect kernel slab-cache allocation.",
  "tpm2_pcrread": "Read selected TPM platform-configuration registers.",
  "tpm2_getcap": "Read TPM capability information.",
  "tpm2_quote": "Produce a TPM-signed attestation quote for a selected key and PCR set.",
  "tpm2_checkquote": "Verify a TPM attestation quote against supplied verification material.",
  "auditctl": "Inspect or change Linux audit rules and audit subsystem configuration.",
  "aureport": "Summarize selected Linux audit records.",
  "ausearch": "Search Linux audit records by explicit criteria.",
  "cryptsetup": "Inspect or administer encrypted volumes; destructive/key-sensitive subcommands require review.",
  "systemctl": "Inspect or control systemd units on the selected host.",
  "dmesg": "Read the kernel ring buffer where access is permitted.",
  "journalctl": "Read selected systemd journal records.",
  "virsh": "Inspect or administer libvirt objects in an explicit connection context.",
  "kubectl": "Inspect or change Kubernetes resources in an explicit cluster and namespace context.",
  "lsblk": "List block devices and their reported relationships.",
  "findmnt": "Inspect mount-table and filesystem information.",
  "swapon": "Inspect or activate swap; only reviewed inspection examples are routine diagnostics.",
  "ip": "Inspect or change network links, addresses and routes.",
  "systemd-detect-virt": "Report detected virtualization or container context; not a trusted asset inventory.",
  "timeout": "Bound a host command run; a timeout does not guarantee blocked kernel work has stopped.",
  "tee": "Copy input to output and selected files; writing changes the target file."
};
const host=HOST_TOOLS.map(name=>({name,hostOnly:true,summary:descriptions[name],usage:name+' [host arguments]',async run(ctx){ctx.stderr.write(`UNAVAILABLE: ${name} requires an authorized host/guest/management adapter. No host process was executed. Use dc list ${name}.\n`);return 69;}}));
export default [...host,
 {name:'commands',summary:'search all built-in commands, aliases and data-center references',usage:'commands [search terms]',async run(ctx){const q=ctx.args.join(' ').toLowerCase();for(const e of describeRegistry(ctx.registry,ctx.aliases)){if(!q||[e.id,e.name,e.syntax,e.description].join(' ').toLowerCase().includes(q))ctx.stdout.write(`${e.id} | ${e.name} | ${e.risk} | ${e.availability}\n  ${e.description}\n`);}return 0;}},
 {name:'dc',summary:'browse and check the hardened data-center command catalog',usage:'dc [list [query]|show ID|check ID|run ID|status]',async run(ctx){
  const sub=ctx.args[0]||'list';
  if(sub==='status'){ctx.stdout.write(JSON.stringify({scope:'SPIRAL virtual environment only',profile:ctx.host.profile?.(),storage:ctx.host.disk?.(),files:ctx.vfs.usage(),physicalHardware:'UNAVAILABLE',hostExecution:false,dataCenterEntries:DATACENTER.length},null,2)+'\n');return 0;}
  if(sub==='list'){const q=ctx.args.slice(1).join(' ').toLowerCase();for(const e of DATACENTER){if(!q||JSON.stringify(e).toLowerCase().includes(q))ctx.stdout.write(`${e.id} | ${e.risk} | ${e.availability} | ${e.description}\n`);}return 0;}
  const e=lookup(ctx.args[1]);if(!e||!['show','check','run'].includes(sub)){ctx.stderr.write('usage: dc list [query] | dc show/check/run ID | dc status\n');return 2;}
  ctx.stdout.write(`${e.id} — ${e.description}\nRisk: ${e.risk}\nPlane: ${e.plane}\nStatus: ${e.availability}\n${e.syntax}\n${e.note}\n`);
  if(sub==='show')return 0;
  ctx.stderr.write('NOT EXECUTED: host tooling, identity verification, privileges and maintenance controls are not provided by this virtual console.\n');return 69;
 }},
 {name:'sync',summary:'commit the virtual filesystem to its configured store',usage:'sync',async run(ctx){await ctx.host.sync?.();ctx.stdout.write('Virtual storage commit completed. Not a host filesystem sync.\n');return 0;}},
 {name:'hostname',summary:'print the configured virtual host label',usage:'hostname',async run(ctx){ctx.stdout.write((ctx.env.get('HOST')||'virtual0')+'\n');return 0;}},
 {name:'id',summary:'print the virtual operator identity (not an OS account)',usage:'id',async run(ctx){ctx.stdout.write(`virtual-user=${ctx.env.get('USER')} (no OS uid or privileges)\n`);return 0;}},
];
