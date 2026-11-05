"use strict";
const $ = id => document.getElementById(id);
const mapCanvas = $("map");
const mapContext = mapCanvas.getContext("2d", {alpha:false});
const frame = $("map-frame");
const format = value => Number(value || 0).toLocaleString("en-US",{maximumFractionDigits:1});
const compact = value => Number(value || 0).toLocaleString("en-US",{notation:"compact",maximumFractionDigits:1});
const mb = bytes => (Number(bytes || 0)/1048576).toFixed(1)+" MB";
let currentMap = {nodes:[],edges:[],domainCells:[],version:0,totalNodes:0,displayedNodes:0};
let currentMetrics = null;
let history = [];
let schedulerHistory = [];
let lastSchedulerRejected = 0;
let lastSchedulerTime = 0;
let selected = null;
let previousCounters = null, lastMetricsAt = 0;
let camera = {x:0,y:0,scale:0.8}, cameraInitialized = false;
let requestController = null, mapTimeout = null, dragging = null, wasDragging = false;
let previousTraffic = new Map(), heat = new Map();

function toast(message) {
  const element=$("toast"); element.textContent=message; element.style.display="block";
  clearTimeout(toast.timer); toast.timer=setTimeout(()=>element.style.display="none",4500);
}
function resizeCanvas(canvas) {
  const rect=canvas.getBoundingClientRect();
  const dpr=Math.min(2,window.devicePixelRatio||1);
  const w=Math.max(1,Math.floor(rect.width*dpr)),h=Math.max(1,Math.floor(rect.height*dpr));
  if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}
  const ctx=canvas.getContext("2d");
  ctx.setTransform(dpr,0,0,dpr,0,0);
  return {ctx,w:rect.width,h:rect.height};
}
function screen(p,w,h){return {x:(p.x-camera.x)*camera.scale+w/2,y:(p.y-camera.y)*camera.scale+h/2};}
function world(x,y){const r=frame.getBoundingClientRect();return {x:(x-r.width/2)/camera.scale+camera.x,y:(y-r.height/2)/camera.scale+camera.y};}
function fitMap() {
  const status=currentMetrics?.benchmark;
  if(!status || !status.worldWidth || !status.worldHeight) return;
  camera.x=status.worldWidth/2;camera.y=status.worldHeight/2;
  camera.scale=Math.max(0.08,Math.min(2,Math.min((frame.clientWidth-70)/status.worldWidth,(frame.clientHeight-70)/status.worldHeight)));
  cameraInitialized=true;scheduleMap();drawMap();
}
function scheduleMap(){clearTimeout(mapTimeout);mapTimeout=setTimeout(loadMap,160);}
async function loadMap() {
  if(!currentMetrics?.nodeCount) return;
  if(requestController)requestController.abort();
  requestController=new AbortController();
  const r=frame.getBoundingClientRect(),pad=35/camera.scale;
  const left=camera.x-r.width/(2*camera.scale)-pad,top=camera.y-r.height/(2*camera.scale)-pad;
  const right=camera.x+r.width/(2*camera.scale)+pad,bottom=camera.y+r.height/(2*camera.scale)+pad;
  const params=new URLSearchParams({
    minX:Math.floor(left),minY:Math.floor(top),
    maxX:Math.ceil(right),maxY:Math.ceil(bottom),z:0,limit:6000
  });
  try {
    const response=await fetch("/api/map?"+params,{signal:requestController.signal});
    if(!response.ok)throw Error("Topology snapshot HTTP "+response.status);
    const map=await response.json();
    const nextHeat=new Map(), newTraffic=new Map();
    for(const node of map.nodes){
      // Cumulative route-traffic samples from NetDM.Core. Difference is visible activity.
      const old=previousTraffic.get(node.id)??node.traffic;
      nextHeat.set(node.id,Math.max(0,node.traffic-old));
      newTraffic.set(node.id,node.traffic);
    }
    previousTraffic=newTraffic;
    heat=nextHeat;
    currentMap=map;
    $("map-count").textContent=format(map.displayedNodes)+" / "+format(map.totalNodes)+" visible nodes";
    $("map-status").textContent=map.truncated
      ? "VIEWPORT LIMIT REACHED · ZOOM IN"
      : map.domainCells?.length
        ? "SHARED MEDIUM · INDEXED DOMAIN CELLS"
        : "SPATIAL INDEX · VIEWPORT QUERY";
    $("graph-version").textContent=format(map.version);
    $("map-empty").style.display=map.totalNodes?"none":"block";
    drawMap();
  }catch(error){if(error.name!=="AbortError")console.warn(error);}
}
function drawMap(){
  const {ctx,w,h}=resizeCanvas(mapCanvas);
  ctx.fillStyle="#0a1322";ctx.fillRect(0,0,w,h);
  // Sparse spatial reference grid that adapts to zoom level.
  let step=32;
  while(step*camera.scale<48)step*=2;
  while(step*camera.scale>120)step/=2;
  const left=camera.x-w/2/camera.scale,right=camera.x+w/2/camera.scale;
  const top=camera.y-h/2/camera.scale,bottom=camera.y+h/2/camera.scale;
  ctx.beginPath();ctx.lineWidth=1;ctx.strokeStyle="#17263a";
  for(let x=Math.floor(left/step)*step;x<right;x+=step){
    const px=(x-camera.x)*camera.scale+w/2;ctx.moveTo(px,0);ctx.lineTo(px,h);
  }
  for(let y=Math.floor(top/step)*step;y<bottom;y+=step){
    const py=(y-camera.y)*camera.scale+h/2;ctx.moveTo(0,py);ctx.lineTo(w,py);
  }
  ctx.stroke();
  // Shared medium shows occupied channel cells, not imaginary N×N radio edges.
  // The physical network graph remains sparse even for thousands of co-located devices.
  if(currentMap.domainCells?.length){
    const tileSize=32*camera.scale;
    for(const cell of currentMap.domainCells){
      const p=screen({x:cell.x*32,y:cell.y*32},w,h);
      if(p.x>w||p.y>h||p.x+tileSize<0||p.y+tileSize<0)continue;
      const strength=Math.min(.38,.10+Math.log2(cell.count+1)*.035);
      const cool=cell.channel%2===0;
      ctx.fillStyle=cool
        ? "rgba(91,146,253,"+strength+")"
        : "rgba(47,213,191,"+strength+")";
      ctx.fillRect(p.x,p.y,tileSize,tileSize);
      if(tileSize>12){
        ctx.strokeStyle=cool?"rgba(93,151,255,.34)":"rgba(67,230,202,.32)";
        ctx.lineWidth=1;ctx.strokeRect(p.x,p.y,tileSize,tileSize);
      }
    }
  }
  if(!currentMap.nodes.length){$("viewport-text").textContent="NO ACTIVE CELLS";return;}
  const byId=new Map(currentMap.nodes.map(n=>[n.id,n]));
  const stroke=Math.max(.5,Math.min(1.25,camera.scale*.7));
  ctx.lineWidth=stroke;
  ctx.strokeStyle=currentMetrics?.benchmark?.options?.wirelessEnabled
    ? "rgba(62,203,216,.42)" : "rgba(74,115,162,.27)";
  ctx.beginPath();
  for(const e of currentMap.edges){
    const a=byId.get(e.a),b=byId.get(e.b);
    if(!a||!b)continue;
    const p=screen(a,w,h),q=screen(b,w,h);
    if((p.x<0&&q.x<0)||(p.x>w&&q.x>w)||(p.y<0&&q.y<0)||(p.y>h&&q.y>h))continue;
    ctx.moveTo(p.x,p.y);ctx.lineTo(q.x,q.y);
  }
  ctx.stroke();
  const normalRadius=camera.scale>1.5?2.8:2;
  ctx.fillStyle="#5ea6d5";ctx.beginPath();
  for(const node of currentMap.nodes){
    if(selected===node.id || (heat.get(node.id)||0)>0)continue;
    const p=screen(node,w,h);
    if(p.x<-5||p.x>w+5||p.y<-5||p.y>h+5)continue;
    ctx.moveTo(p.x+normalRadius,p.y);ctx.arc(p.x,p.y,normalRadius,0,Math.PI*2);
  }
  ctx.fill();
  for(const node of currentMap.nodes){
    const activity=heat.get(node.id)||0;
    if(activity<=0&&node.id!==selected)continue;
    const p=screen(node,w,h);
    if(p.x<-10||p.x>w+10||p.y<-10||p.y>h+10)continue;
    const focus=node.id===selected;
    const radius=focus?6:Math.min(5,2.4+Math.log2(1+activity)*.7);
    if(focus){
      ctx.strokeStyle="#d4f6ff";ctx.lineWidth=1.5;ctx.beginPath();
      ctx.arc(p.x,p.y,10,0,Math.PI*2);ctx.stroke();
      const range=currentMetrics?.benchmark?.options?.wirelessEnabled
        ? currentMetrics.benchmark.options.radioRange : 0;
      if(range){
        ctx.strokeStyle="rgba(85,219,225,.35)";
        ctx.lineWidth=1;
        ctx.beginPath();ctx.arc(p.x,p.y,range*camera.scale,0,Math.PI*2);ctx.stroke();
      }
    }
    ctx.fillStyle=focus?"#e5fbff":"#ffb77b";ctx.beginPath();ctx.arc(p.x,p.y,radius,0,Math.PI*2);ctx.fill();
  }
  $("viewport-text").textContent="X "+Math.round(left)+" → "+Math.round(right)+"   /   Y "+Math.round(top)+" → "+Math.round(bottom);
  if(selected!==null){
    const node=byId.get(selected);
    $("node-details").textContent=node
      ? "NODE #"+node.id+" · "+node.links+
        (currentMetrics?.benchmark?.options?.radioModel==="shared"?" WIRED LINKS":" LINKS")+
        " · "+node.traffic+" TRAFFIC SAMPLES · "+node.x+", "+node.y
      : "SELECTED NODE OUTSIDE VIEWPORT";
  }
}
function pointerPosition(event){const r=frame.getBoundingClientRect();return{x:event.clientX-r.left,y:event.clientY-r.top};}
mapCanvas.addEventListener("pointerdown",event=>{
  if(event.button!==0)return;
  mapCanvas.setPointerCapture(event.pointerId);
  dragging={startX:event.clientX,startY:event.clientY,cx:camera.x,cy:camera.y};
  wasDragging=false;
});
mapCanvas.addEventListener("pointermove",event=>{
  if(!dragging)return;
  const dx=event.clientX-dragging.startX,dy=event.clientY-dragging.startY;
  if(Math.abs(dx)+Math.abs(dy)>4)wasDragging=true;
  camera.x=dragging.cx-dx/camera.scale;camera.y=dragging.cy-dy/camera.scale;
  drawMap();
});
mapCanvas.addEventListener("pointerup",event=>{
  const moved=wasDragging;dragging=null;
  if(moved){scheduleMap();return;}
  const p=pointerPosition(event);
  let closest=null,distance=11*11;
  const r=frame.getBoundingClientRect();
  for(const node of currentMap.nodes){
    const screenPoint=screen(node,r.width,r.height);
    const d=(screenPoint.x-p.x)**2+(screenPoint.y-p.y)**2;
    if(d<distance){closest=node;distance=d;}
  }
  selected=closest?.id??null;
  if(selected===null)$("node-details").textContent="SELECT A NODE TO INSPECT IT";
  drawMap();
});
mapCanvas.addEventListener("wheel",event=>{
  event.preventDefault();
  const p=pointerPosition(event),before=world(p.x,p.y);
  camera.scale=Math.max(.055,Math.min(8,camera.scale*Math.exp(-event.deltaY*.001)));
  const after=world(p.x,p.y);
  camera.x+=before.x-after.x;camera.y+=before.y-after.y;
  drawMap();scheduleMap();
},{passive:false});
$("fit").addEventListener("click",fitMap);
const observe=new ResizeObserver(()=>{drawMap();drawCharts();drawSchedulerCharts();drawComparisonPlot();});
observe.observe(frame);
for(const id of ["routes-chart","load-chart","latency-chart","explored-chart","gc-chart","memory-chart","links-chart","reachability-chart","packets-chart","packet-queue-chart","reroute-pressure-chart","retry-backoff-chart","tick-health-chart","tick-timing-chart","rejections-chart","sched-queue-chart","sched-reject-chart","compare-chart"])observe.observe($(id));

function plot(id,series,color,extra){
  const canvas=$(id),{ctx,w,h}=resizeCanvas(canvas);
  ctx.clearRect(0,0,w,h);
  ctx.strokeStyle="#203049";ctx.lineWidth=1;
  for(let i=0;i<=3;i++){let y=8+i*(h-23)/3;ctx.beginPath();ctx.moveTo(0,y);ctx.lineTo(w,y);ctx.stroke();}
  if(series.length<2)return;
  const full=extra ? series.concat(extra) : series;
  const max=Math.max(1,...full.flatMap(s=>s.values).filter(Number.isFinite));
  const x=(i)=>(i/(Math.max(1,series[0].values.length-1)))*w;
  const y=(v)=>h-15-(Math.max(0,v)/max)*(h-25);
  for(const s of full){
    const values=s.values;ctx.beginPath();ctx.strokeStyle=s.color||color;ctx.lineWidth=s.width||2;
    for(let i=0;i<values.length;i++){const px=x(i),py=y(values[i]||0);if(i===0)ctx.moveTo(px,py);else ctx.lineTo(px,py);}
    ctx.stroke();
  }
}
function drawCharts(){
  const last=history[history.length-1];
  plot("routes-chart",[{values:history.map(x=>x.actualRoutesPerSecond),color:"#60d9e6"}],"#60d9e6");
  plot("load-chart",[
    {values:history.map(x=>x.offeredRoutesPerSecond||0),color:"#85acff"},
    {values:history.map(x=>x.admittedRoutesPerSecond||0),color:"#60d9e6"},
    {values:history.map(x=>x.rejectedRoutesPerSecond||0),color:"#f29e81"}
  ],"#85acff");
  $("chart-load").textContent=last
    ? format(last.offeredRoutesPerSecond||0)+" / "+
      format(last.admittedRoutesPerSecond||0)+" / "+
      format(last.rejectedRoutesPerSecond||0)
    : "—";
  plot("latency-chart",[
    {values:history.map(x=>x.p50Us),color:"#4a85cc",width:1.1},
    {values:history.map(x=>x.p95Us),color:"#5bd5db",width:2},
    {values:history.map(x=>x.p99Us),color:"#f3a572",width:1.3}
  ],"#5bd5db");
  plot("memory-chart",[{values:history.map(x=>x.managedBytes/1048576),color:"#87a5ff"}],"#87a5ff");
  plot("explored-chart",[{values:history.map(x=>x.exploredPerRoute||0),color:"#69d5e2"}],"#69d5e2");
  plot("gc-chart",[{values:history.map(x=>x.gcPauseMillisecondsPerSecond||0),color:"#eea576"}],"#eea576");
  $("chart-explored").textContent=last
    ?format(last.exploredPerRoute||0)+" nodes / "+format((last.allocatedBytesPerRoute||0)/1024)+" KiB":"—";
  $("chart-gc").textContent=last?format(last.gcPauseMillisecondsPerSecond||0)+" ms/s":"—";
  plot("reachability-chart",[
    {values:history.map(x=>x.foundPercent||0),color:"#56dfbc"},
    {values:history.map(x=>x.mediumConfiguredDevices>0
      ?100*x.mediumConfiguredDevices/Math.max(1,x.nodeCount):0),color:"#729cf6",width:1.2}
  ],"#56dfbc");
  plot("packets-chart",[
    {values:history.map(x=>x.packetsDeliveredPerSecond||0),color:"#65dbb2"},
    {values:history.map(x=>x.packetsDroppedPerSecond||0),color:"#ed8d79"},
    {values:history.map(x=>x.packetsExpiredPerSecond||0),color:"#af9adb"}
  ],"#65dbb2");
  plot("packet-queue-chart",[
    {values:history.map(x=>x.packetsQueued||0),color:"#82aafc"},
    {values:history.map(x=>x.packetWirelessHopsPerSecond||0),color:"#f2be7a"},
    {values:history.map(x=>x.spatialSuccessfulHopsPerSecond||0),color:"#5ddbb7"}
  ],"#82aafc");
  $("chart-packets").textContent=last?
    format(last.packetsDeliveredPerSecond||0)+" / "+
    format(last.packetsDroppedPerSecond||0)+" · p95 "+
    format((last.deliveryP95AgeTicks||0)/10)+"s · first-hop p95 "+
    format((last.packetFirstHopP95Ticks||0)/10)+"s":"—";
  $("chart-packet-queue").textContent=last?format(last.packetsQueued||0)+" queued":"—";
  plot("reroute-pressure-chart",[
    {values:history.map(x=>x.reroutesQueued||0),color:"#8d9dff"},
    {values:history.map(x=>x.reroutesInFlight||0),color:"#77d8d6"},
    {values:history.map(x=>x.rerouteStartedPerSecond||0),color:"#eab47e"}
  ],"#8d9dff");
  plot("retry-backoff-chart",[
    {values:history.map(x=>x.noRouteBackoffsPerSecond||0),color:"#e9a674"},
    {values:history.map(x=>x.localChangeWakeupsPerSecond||0),color:"#64d9c6"},
    {values:history.map(x=>x.rerouteCooldownSkipsPerSecond||0),color:"#8d9dff"}
  ],"#e9a674");
  $("chart-retry-backoff").textContent=last
    ?format(last.noRouteBackoffsPerSecond||0)+" backoffs/s · "+
      format(last.localChangeWakeupsPerSecond||0)+" wakes/s · "+
      format(last.averageNoRouteBackoffTicks||0)+" ticks avg"
    :"—";
  plot("tick-health-chart",[
    {values:history.map(x=>x.tickExecutedPerSecond||0),color:"#5adaca"},
    {values:history.map(x=>x.packetSkippedTicksPerSecond||0),color:"#ef9e78"},
    {values:history.map(x=>x.packetTickAdmissionFailuresPerSecond||0),color:"#ee658b"}
  ],"#5adaca");
  plot("tick-timing-chart",[
    {values:history.map(x=>x.tickExecutionP95Milliseconds||0),color:"#5adaca"},
    {values:history.map(x=>x.tickStartLagP95Milliseconds||0),color:"#7b9ff6"},
    {values:history.map(x=>x.tickBudgetStopsPerSecond||0),color:"#e9a674"}
  ],"#5adaca");
  $("chart-tick-timing").textContent=last
    ?format(last.tickExecutionP95Milliseconds||0)+"ms work · "+
      format(last.tickStartLagP95Milliseconds||0)+"ms lag · "+
      format(last.tickBudgetStopsPerSecond||0)+" budget/s":"—";
  $("chart-reroute-pressure").textContent=last
    ?format(last.reroutesQueued||0)+" pending · "+format(last.reroutesInFlight||0)+
      " running · wait p95 "+format((last.rerouteWaitP95Ticks||0)/10)+"s"
    :"—";
  $("chart-tick-health").textContent=last
    ?format(last.tickExecutedPerSecond||0)+" ticks/s · "+
      format(last.packetSkippedTicksPerSecond||0)+" missed/s · "+
      format(last.packetForwardingP95Milliseconds||0)+"ms forward p95":"—";
  plot("rejections-chart",[
    {values:history.map(x=>x.rejectedTooLargePerSecond||0),color:"#f19f72"},
    {values:history.map(x=>x.rejectedCapacityPerSecond||0),color:"#e76373"},
    {values:history.map(x=>x.rejectedStalePerSecond||0),color:"#689eff"},
    {values:history.map(x=>(x.rejectedInvalidPerSecond||0)+(x.rejectedOldTickPerSecond||0)),color:"#9d86dd"}
  ],"#f19f72");
  $("chart-reachability").textContent=last?format(last.foundPercent||0)+"%":"—";
  $("chart-radio-reasons").textContent=last
    ? format((last.rejectedTooLargePerSecond||0)+(last.rejectedCapacityPerSecond||0)+
             (last.rejectedStalePerSecond||0)+(last.rejectedInvalidPerSecond||0)+
             (last.rejectedOldTickPerSecond||0))+"/s":"—";
  const shared=currentMetrics?.benchmark?.options?.radioModel==="shared";
  const accepted=history.map(x=>x.acceptedTransfersPerSecond||0);
  const rejected=history.map(x=>x.rejectedTransfersPerSecond||0);
  plot("links-chart",[
    {values:shared?accepted:history.map(x=>x.radioLinksAddedPerSecond||0),color:"#5adaba"},
    {values:shared?rejected:history.map(x=>x.radioLinksRemovedPerSecond||0),color:"#f1aa82"},
    {values:shared?history.map(x=>x.revalidatedTransfersPerSecond||0):[],color:"#79a8ff",width:1.4}
  ],"#5adaba");
  $("churn-title").textContent=shared?"Shared airtime":"Link churn";
  $("churn-subtitle").textContent=shared
    ? "Accepted / rejected transfers per second"
    : "New / lost proximity links per second";
  $("chart-rps").textContent=last?format(last.actualRoutesPerSecond)+"/s":"—";
  $("chart-latency").textContent=last?format(last.p95Us)+" µs":"—";
  $("chart-memory").textContent=last?mb(last.managedBytes):"—";
  $("chart-link-churn").textContent=last
    ? shared
      ? format(last.acceptedTransfersPerSecond||0)+" / "+format(last.rejectedTransfersPerSecond||0)
      : "+"+format(last.radioLinksAddedPerSecond||0)+" / -"+format(last.radioLinksRemovedPerSecond||0)
    : "—";
}

function drawSchedulerCharts(){
  const samples=schedulerHistory;
  plot("sched-queue-chart",[
    {values:samples.map(x=>x.depth),color:"#59d6e4"},
    {values:samples.map(x=>x.running),color:"#f2b47e",width:1.1}
  ],"#59d6e4");
  plot("sched-reject-chart",[
    {values:samples.map(x=>x.rejections),color:"#ee8e81"},
    {values:samples.map(x=>x.waitP95),color:"#659dff",width:1.25}
  ],"#ee8e81");
  const last=samples[samples.length-1];
  $("sched-chart-depth").textContent=last?format(last.depth)+" waiting":"—";
  $("sched-chart-rejections").textContent=last?format(last.rejections)+"/s":"—";
}
function ingestScheduler(scheduler){
  if(!scheduler)return;
  $("sched-depth").textContent=format(scheduler.queued)+" / "+format(scheduler.peakQueued);
  $("sched-queues").textContent="C "+format(scheduler.criticalQueued)+
    " · I "+format(scheduler.interactiveQueued)+
    " · B "+format(scheduler.backgroundQueued);
  $("sched-workers").textContent=format(scheduler.running)+" / "+format(scheduler.workers);
  $("sched-budget").textContent="soft budget "+format(scheduler.budgetMillisecondsPer100Ms)+" ms / 100 ms";
  $("sched-rejected").textContent=format(scheduler.rejected);
  $("sched-accepted").textContent=format(scheduler.accepted)+" accepted · "+
    format(scheduler.failed)+" failed · "+format(scheduler.cancelled)+" cancelled";
  $("sched-wait").textContent=format(scheduler.queueWaitP95Us)+" µs";
  $("sched-processing").textContent=format(scheduler.workP95Us)+" µs work p95";
  const now=performance.now();
  const span=lastSchedulerTime?Math.max(.001,(now-lastSchedulerTime)/1000):1;
  const change=Math.max(0,scheduler.rejected-lastSchedulerRejected)/span;
  lastSchedulerRejected=scheduler.rejected;lastSchedulerTime=now;
  schedulerHistory.push({
    depth:scheduler.queued,running:scheduler.running,
    rejections:change,waitP95:scheduler.queueWaitP95Us
  });
  if(schedulerHistory.length>180)schedulerHistory.shift();
  drawSchedulerCharts();
}

async function pollMetrics(){
  try{
    const response=await fetch("/api/metrics",{cache:"no-store"});
    if(!response.ok)throw Error(response.status);
    const data=await response.json();
    $("connection").classList.remove("offline");
    $("connection").innerHTML='<span class="status-dot"></span> HOST ONLINE';
    const bench=data.benchmark;
    const status=bench.status.toUpperCase();
    $("run-status").textContent=status;
    $("run-elapsed").textContent=bench.startedUtc?format(bench.elapsedSeconds)+"s ELAPSED":"NO ACTIVE TEST";
    $("benchmark-indicator").className="pulse "+(bench.status==="running"?"running":bench.status==="building"?"building":"");
    $("start").disabled=bench.status==="running"||bench.status==="building"||bench.status==="stopping";
    ingestScheduler(data.scheduler);
    $("kpi-nodes").textContent=compact(data.nodeCount);
    $("kpi-edges").textContent=format(data.edgeCount)+" connections";
    $("kpi-p95").textContent=format(data.runtime.p95Microseconds)+" µs";
    $("kpi-cache-count").textContent=format(data.runtime.cacheEntries)+" cached routes";
    const shared=bench.sharedMedium;
    $("kpi-radio-label").textContent=shared?"RADIO DOMAINS":"RADIO LINKS";
    $("kpi-radio").textContent=shared
      ? format(shared.domains)
      : bench.radio?compact(bench.radio.activeWirelessLinks):"—";
    $("kpi-radio-events").textContent=shared&&bench.packetTransport
      ? format(bench.packetTransport.delivered)+" delivered · "+
        format(bench.packetTransport.queued)+" queued · "+
        format(bench.packetTransport.rerouteQueued||0)+" reroute pending · p95 "+
        format((bench.packetTransport.deliveryAgeP95Ticks||0)/10)+"s · "+
        format(shared?.spatialActiveCells||0)+" airtime cells"
      : shared
      ? format(shared.configuredDevices)+" devices · "+format(shared.occupiedCells)+" cells · "+
        format(shared.acceptedRevalidatedTransfers)+" revalidated · "+
        format(shared.rejectedTransfers)+" denied"
      : bench.radio
        ? "+"+format(bench.radio.totalWirelessLinksAdded)+" / -"+format(bench.radio.totalWirelessLinksRemoved)+" lifetime"
        : "Wired graph / no proximity links";
    $("kpi-version").textContent="topology v"+format(data.version);
    $("connectivity-version").textContent=format(data.connectivityVersion);
    $("kpi-target").textContent="target "+format(bench.options?.routesPerSecond||0)+"/s";
    let dt=(performance.now()-lastMetricsAt)/1000;
    if(previousCounters&&dt>0){
      const routes=data.runtime.routes-previousCounters.routes;
      const mutations=data.runtime.mutations-previousCounters.mutations;
      const hits=data.runtime.cacheHits-previousCounters.cacheHits;
      $("kpi-rps").textContent=compact(routes/dt);
      $("kpi-mps").textContent=compact(mutations/dt);
      $("kpi-cache").textContent=routes>0?format(hits/routes*100)+"%":"0%";
    }
    previousCounters=data.runtime;lastMetricsAt=performance.now();
    const wasEmpty=!currentMetrics?.nodeCount;
    currentMetrics=data;
    if(!cameraInitialized && bench.worldWidth>0)fitMap();
    if(wasEmpty && data.nodeCount>0)setTimeout(loadMap,100);
  }catch(error){
    $("connection").classList.add("offline");
    $("connection").innerHTML='<span class="status-dot"></span> HOST OFFLINE';
  }
}
async function pollHistory(){
  try{
    const response=await fetch("/api/history",{cache:"no-store"});
    if(response.ok){history=await response.json();drawCharts();}
  }catch{}
}
$("bench-form").addEventListener("submit",async event=>{
  event.preventDefault();
  const options={
    nodes:Number($("nodes").value),routesPerSecond:Number($("routes").value),
    movesPerSecond:Number($("moves").value),linkChangesPerSecond:Number($("links").value),
    durationSeconds:Number($("duration").value),seed:Number($("seed").value),
    wirelessEnabled:$("network-mode").value!=="wired",
    radioModel:$("network-mode").value==="shared"?"shared":"pairwise",
    mediumBytesPerTick:Number($("medium-budget").value),
    sharedSearch:$("shared-search").value,
    packetTransportEnabled:$("packet-delivery").value==="hopwise" &&
      $("network-mode").value==="shared",
    spatialReuseEnabled:$("packet-airtime").value==="spatial",
    packetMaxAgeTicks:Number($("packet-ttl").value),
    packetRerouteCpuBudgetMs:Number($("packet-reroute-budget").value),
    packetRerouteWorkers:Number($("packet-reroute-workers").value),
    dedicatedPacketTicks:$("packet-tick-mode").value==="dedicated",
    packetTickWorkBudgetMs:Number($("packet-tick-budget").value),
    packetMaxNoRouteRetryDelayTicks:Number($("packet-backoff-cap").value),
    packetRerouteQueueRetryDelayTicks:Number($("packet-reroute-cooldown").value),
    radioRange:Number($("radio-range").value)
  };
  try{
    const response=await fetch("/api/benchmark/start",{
      method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(options)
    });
    if(!response.ok){const error=await response.json();throw Error(error.error||response.statusText);}
    cameraInitialized=false;selected=null;previousTraffic.clear();heat.clear();
    previousCounters=null;lastMetricsAt=0;history=[];
    toast("Benchmark started. Building network topology.");
    await pollMetrics();setTimeout(loadMap,300);
  }catch(error){toast("Unable to start benchmark: "+error.message);}
});
$("stop").addEventListener("click",async()=>{
  try{await fetch("/api/benchmark/stop",{method:"POST"});toast("Stop requested.");}
  catch(error){toast(error.message);}
});
const presets={
  small:{nodes:500,routes:100,moves:20,links:2,duration:30},
  medium:{nodes:2500,routes:300,moves:120,links:4,duration:60},
  heavy:{nodes:12000,routes:1500,moves:1000,links:30,duration:120}
};
document.querySelectorAll("[data-preset]").forEach(button=>button.addEventListener("click",()=>{
  document.querySelectorAll("[data-preset]").forEach(b=>b.classList.remove("active"));
  button.classList.add("active");
  for(const [id,value] of Object.entries(presets[button.dataset.preset]))$(id).value=value;
}));
$("export").addEventListener("click",async event=>{
  event.preventDefault();
  const payload={exportedAt:new Date().toISOString(),metrics:currentMetrics,history,topology:currentMap};
  const blob=new Blob([JSON.stringify(payload,null,2)],{type:"application/json"});
  const url=URL.createObjectURL(blob);
  const link=document.createElement("a");link.href=url;link.download="netdm-benchmark-"+Date.now()+".json";
  document.body.append(link);link.click();link.remove();
  setTimeout(()=>URL.revokeObjectURL(url),1000);
});
pollMetrics();pollHistory();drawMap();
setInterval(pollMetrics,950);
setInterval(pollHistory,1600);
setInterval(()=>{if(!dragging&&currentMetrics?.nodeCount>0)loadMap();},1600);


let comparisonState=null;
let lastComparisonReportId="";
function drawComparisonPlot(){
  const canvas=$("compare-chart");
  if(!canvas)return;
  const {ctx,w,h}=resizeCanvas(canvas);
  ctx.clearRect(0,0,w,h);
  const algorithms=comparisonState?.report?.algorithms;
  if(!algorithms?.length){
    ctx.fillStyle="#8295ae";ctx.font="11px Segoe UI, sans-serif";
    ctx.fillText("Awaiting comparison data",12,h/2);
    return;
  }
  const max=Math.max(1,...algorithms.map(a=>a.p95Us));
  const rowHeight=Math.min(37,(h-10)/algorithms.length);
  algorithms.forEach((algorithm,index)=>{
    const y=7+index*rowHeight;
    const barX=6,barWidth=Math.max(0,w-70);
    ctx.fillStyle="#1c2c41";ctx.fillRect(barX,y,barWidth,12);
    ctx.fillStyle=index===0?"#5a819f":"#58cadb";
    ctx.fillRect(barX,y,barWidth*algorithm.p95Us/max,12);
    ctx.fillStyle="#b8cbe0";ctx.font="10px Segoe UI, sans-serif";
    ctx.fillText(format(algorithm.p95Us)+" µs",barX+barWidth+7,y+10);
    ctx.fillStyle="#829ab6";ctx.font="10px Segoe UI, sans-serif";
    ctx.fillText(algorithm.name,y===-1?0:barX,y+25);
  });
}
function renderComparison(report){
  const rows=$("comparison-rows");
  rows.replaceChildren();
  const best=Math.min(...report.algorithms.map(a=>a.p95Us));
  for(const a of report.algorithms){
    const tr=document.createElement("tr");
    const cells=[
      a.name,format(a.p50Us)+" µs",format(a.p95Us)+" µs",
      format(a.p99Us)+" µs",format(a.meanExploredNodes),
      format(a.meanAllocatedBytes/1024)+" KiB",
      compact(a.serialQueriesPerSecond)
    ];
    cells.forEach((value,index)=>{
      const td=document.createElement("td");
      td.textContent=value;
      if(index===2&&a.p95Us===best)td.className="best";
      tr.appendChild(td);
    });
    rows.appendChild(tr);
  }
  const spatial=report.spatial,movement=report.movement;
  const summaries=[
    "SPATIAL INDEX · "+format(spatial.indexedP95Us)+" µs p95",
    "FULL SCAN · "+format(spatial.scanP95Us)+" µs p95",
    "CSR BUILD · "+format(report.csrBuildMs)+" ms / "+mb(report.csrArrayPayloadBytes)+" arrays",
    "MOVE BATCH · "+format(movement.batchP95Us)+" µs p95"
  ];
  $("comparison-details").replaceChildren(...summaries.map(value=>{
    const span=document.createElement("span");span.textContent=value;return span;
  }));
  drawComparisonPlot();
}
async function pollComparison(){
  try{
    const response=await fetch("/api/comparison",{cache:"no-store"});
    if(!response.ok)return;
    const state=await response.json();
    comparisonState=state;
    $("compare-status").textContent=state.status.toUpperCase();
    $("compare-stage").textContent=state.stage+(state.error?" · "+state.error:"");
    $("compare-progress").textContent=format(state.completed)+" / "+format(state.total);
    $("compare-start").disabled=state.status==="running"||state.status==="stopping";
    const id=state.report?.completedAt||"";
    if(id&&id!==lastComparisonReportId){
      lastComparisonReportId=id;
      renderComparison(state.report);
    }
  }catch(error){console.warn("Comparison telemetry error",error);}
}
$("compare-form").addEventListener("submit",async event=>{
  event.preventDefault();
  const options={
    nodes:Number($("compare-nodes").value),
    routes:Number($("compare-routes").value),
    viewportQueries:Number($("compare-viewports").value),
    moveBatches:32,
    seed:Number($("compare-seed").value)
  };
  try{
    const response=await fetch("/api/comparison/start",{
      method:"POST",headers:{"Content-Type":"application/json"},
      body:JSON.stringify(options)
    });
    if(!response.ok){
      const body=await response.json();
      throw Error(body.error||response.statusText);
    }
    lastComparisonReportId="";
    $("comparison-rows").innerHTML='<tr><td colspan="7" class="no-data">Running controlled comparison…</td></tr>';
    toast("Algorithm comparison started.");
    await pollComparison();
  }catch(error){toast("Comparison rejected: "+error.message);}
});
$("compare-stop").addEventListener("click",async()=>{
  try{
    await fetch("/api/comparison/stop",{method:"POST"});
    await pollComparison();
  }catch(error){toast(error.message);}
});
$("compare-export").addEventListener("click",()=>{
  const report=comparisonState?.report;
  if(!report){toast("No completed comparison to export.");return;}
  const blob=new Blob([JSON.stringify(report,null,2)],{type:"application/json"});
  const url=URL.createObjectURL(blob);
  const a=document.createElement("a");a.href=url;
  a.download="netdm-routing-comparison-"+Date.now()+".json";
  document.body.append(a);a.click();a.remove();
  setTimeout(()=>URL.revokeObjectURL(url),1000);
});
setInterval(pollComparison,1300);
pollComparison();

$("network-mode").addEventListener("change",()=>{
  const selectedMode=$("network-mode").value;
  const enabled=selectedMode!=="wired";
  $("radio-options").hidden=!enabled;
  $("medium-budget-row").hidden=selectedMode!=="shared";
  $("links").labels[0].querySelector("small").textContent=enabled
    ? "radio toggles / sec" : "0 – 1,000";
  document.querySelector(".radio-description").textContent=
    selectedMode==="shared"
      ? "Radio devices share channel airtime without generating pairwise graph edges. View domain tiles, routing, and congestion."
      : "Nodes connect through physical proximity. Link changes simulate radio outages.";
});

let suiteState=null;
async function pollSuite(){
  try{
    const response=await fetch("/api/benchmark-suite",{cache:"no-store"});
    if(!response.ok)return;
    const state=await response.json();
    suiteState=state;
    const running=state.status==="running"||state.status==="stopping";
    $("suite-start").disabled=running;
    $("suite-stop").disabled=!running||state.status==="stopping";
    const titles={idle:"READY",running:"RUNNING",stopping:"STOPPING",
      complete:"ALL THREE COMPLETE",stopped:"STOPPED",failed:"FAILED"};
    $("suite-status").textContent=titles[state.status]||state.status.toUpperCase();
    const opts=state.options;
    if(opts){
      $("suite-settings").textContent=
        format(opts.nodes)+" nodes · "+format(opts.routesPerSecond)+
        " requested routes/s · seed "+opts.seed+" · radio range "+opts.radioRange+
        " · "+format(opts.mediumBytesPerTick)+" bytes/tick · "+
        opts.durationSeconds+" seconds per scenario · "+
        (opts.sharedSearch==="astar"?"A*":"Dijkstra")+" · "+
        (opts.packetTransportEnabled?"hop-by-hop packets":"legacy airtime")+" · "+
        (opts.spatialReuseEnabled?"spatial sharing":"global channel")+
        " · TTL "+opts.packetMaxAgeTicks+" ticks · "+
        "reroute "+opts.packetRerouteWorkers+" workers / "+
        opts.packetRerouteCpuBudgetMs+"ms per 100ms · ticks "+
        (opts.dedicatedPacketTicks?"dedicated":"global queue")+
        " · tick work "+opts.packetTickWorkBudgetMs+"ms · "+
        "no-route cap "+opts.packetMaxNoRouteRetryDelayTicks+" ticks.";
    }
    const rows=document.querySelectorAll("[data-suite-case]");
    for(const row of rows){
      const number=Number(row.dataset.suiteCase);
      const result=(state.reports||[]).find(r=>r.number===number);
      const active=running&&state.currentScenario===number&&!result;
      row.classList.toggle("running",active);
      row.classList.toggle("ready",!!result);
      row.querySelector(".suite-step-state").textContent=
        result?"COMPLETE":active?"RUNNING":state.status==="failed"&&state.currentScenario===number
          ?"FAILED":state.status==="stopped"&&state.currentScenario===number
            ?"INTERRUPTED":"PENDING";
      const slot=row.querySelector(".suite-step-download");
      slot.replaceChildren();
      if(result&&state.runId){
        const link=document.createElement("a");
        link.href="/api/benchmark-suite/download/"+number+
          "?runId="+encodeURIComponent(state.runId);
        link.download=result.fileName;
        link.textContent="DOWNLOAD JSON · "+format(result.bytes/1024)+" KiB";
        slot.append(link);
      }
    }
    const elapsed=running&&currentMetrics?.benchmark?.elapsedSeconds||0;
    const progress=Math.min(100,100*((state.completed||0)+
      (running&&opts?.durationSeconds&&state.currentScenario>state.completed
        ?Math.min(.99,elapsed/opts.durationSeconds):0))/3);
    $("suite-progress-fill").style.width=progress+"%";
    const message=state.error
      ?state.error
      :state.status==="complete"
        ?"All three reports are ready. Download them and attach to ChatGPT."
        :running?"Scenario "+state.currentScenario+"/3 · "+
          (state.currentTitle||"Preparing topology")+
          " · "+format(elapsed)+"s elapsed"
          :state.status==="stopped"
            ?"Stopped. Completed JSON reports remain available."
            :"The suite continues on the host even if you close this tab.";
    $("suite-message").textContent=message;
    const zip=$("suite-zip");
    zip.hidden=state.status!=="complete"||state.reports.length!==3;
    if(!zip.hidden&&state.runId){
      zip.href="/api/benchmark-suite/download-all?runId="+
        encodeURIComponent(state.runId);
      zip.download="netdm-three-scenarios.zip";
    }
  }catch(error){console.warn("Suite telemetry unavailable",error);}
}
$("suite-start").addEventListener("click",async()=>{
  try{
    const response=await fetch("/api/benchmark-suite/start",{
      method:"POST",headers:{"Content-Type":"application/json"},
      body:JSON.stringify({
        sharedSearch:$("suite-search").value,
        packetTransportEnabled:$("suite-delivery").value==="hopwise",
        spatialReuseEnabled:$("suite-airtime").value==="spatial",
        packetMaxAgeTicks:Number($("suite-ttl").value),
        packetRerouteCpuBudgetMs:Number($("suite-reroute-budget").value),
        packetRerouteWorkers:Number($("suite-reroute-workers").value),
        dedicatedPacketTicks:$("suite-tick-mode").value==="dedicated",
        packetTickWorkBudgetMs:Number($("suite-tick-budget").value),
        packetMaxNoRouteRetryDelayTicks:Number($("suite-backoff-cap").value),
        packetRerouteQueueRetryDelayTicks:Number($("suite-reroute-cooldown").value)
      })
    });
    if(!response.ok){
      const payload=await response.json();
      throw Error(payload.error||response.statusText);
    }
    cameraInitialized=false;selected=null;previousTraffic.clear();heat.clear();
    toast("Three-scenario benchmark suite started.");
    await pollSuite();
    await pollMetrics();
  }catch(error){toast("Unable to start suite: "+error.message);}
});
$("suite-stop").addEventListener("click",async()=>{
  try{
    const response=await fetch("/api/benchmark-suite/stop",{method:"POST"});
    if(!response.ok)throw Error(response.statusText);
    await pollSuite();
    toast("Stopping suite after cancelling the active benchmark.");
  }catch(error){toast("Unable to stop suite: "+error.message);}
});
pollSuite();
setInterval(pollSuite,1750);
