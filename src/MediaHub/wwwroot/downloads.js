import {taskMatchesTab, taskMatchesQuery, statusPill, taskProgress} from './motrix-ui.js';
const $ = id => document.getElementById(id);
let tasks = [], tab = 'all', busy = false, timer, currentDirectory = '', parentDirectory = '', localClient = false, localClientLoaded = false;
const selectedIds = new Set();
function notify(text) { $('notice').textContent = text; clearTimeout(timer); timer = setTimeout(() => $('notice').textContent = '', 5000); }
async function api(url, options = {}) {
  const key = localStorage.getItem('mh_api_key') || '';
  const response = await fetch(url, {...options, headers:{'X-MediaHub-Key':key,...options.headers}, signal:AbortSignal.timeout(15000)});
  if (response.status === 401 && !$('loginDialog').open) $('loginDialog').showModal();
  const value = await response.json(); if (!response.ok) throw Error(value.error || '请求失败'); return value;
}
const bytes = value => { if (!Number.isFinite(value) || value < 0) return '—'; const units=['B','KB','MB','GB','TB']; let i=0; while(value>=1024 && i<units.length-1){value/=1024;i++;} return value.toFixed(i ? 1 : 0)+' '+units[i]; };
function button(label, action) { const b=document.createElement('button'); b.textContent=label; b.onclick=action; return b; }
function render() {
  const list = tasks.filter(t => taskMatchesTab(t,tab) && taskMatchesQuery(t,$('searchTasks').value));
  const sort=$('sortTasks').value;list.sort((a,b)=>sort==='priority'?b.priority-a.priority:sort==='name'?a.name.localeCompare(b.name):sort==='speed'?b.downloadSpeed-a.downloadSpeed:sort==='progress'?(b.progress??-1)-(a.progress??-1):new Date(b.createdAt)-new Date(a.createdAt));
  $('taskCount').textContent = `${list.length} / ${tasks.length}`;
  $('speed').textContent = '↓ '+bytes(tasks.reduce((n,t)=>n+t.downloadSpeed,0))+'/s';
  $('empty').hidden = list.length > 0;
  // Preserve the focused operation across polling updates for keyboard users.
  const focus = document.activeElement?.dataset.focus;
  $('taskList').replaceChildren();
  for (const task of list) {
    const row=document.createElement('article'); row.className='task';
    const select=document.createElement('input');select.type='checkbox';select.className='task-select';select.value=task.id;select.checked=selectedIds.has(task.id);select.setAttribute('aria-label','选择 '+task.name);
    const content=document.createElement('div'), title=document.createElement('h3'), path=document.createElement('p');
    title.textContent=task.name; title.className='download-user-content'; path.textContent=task.saveDir; path.className='muted download-user-content';
    content.append(title,path);
    const connections=document.createElement('p');connections.className='muted';
    if(task.kind==='hls'){
      connections.textContent=task.downloadMode==='ffmpeg'?'兼容模式：顺序下载':`HLS 分片：${task.completedSegments||0} / ${task.totalSegments||0} · 并发：${task.activeConnections||0} / ${task.connectionLimit||0}`;
    }else if(task.kind==='direct')connections.textContent=`连接数：${task.activeConnections||0} / ${task.connectionLimit||0}`;
    if(connections.textContent)content.append(connections);
    if (task.error || task.indexError) { const error=document.createElement('p'); error.className='error'; error.textContent=task.error||task.indexError; content.append(error); }
    if (task.indexed) { const p=document.createElement('p'); p.className='muted'; p.textContent='已加入媒体库'; content.append(p); }
    const progress=document.createElement('div'), size=document.createElement('p'); size.className='muted'; size.textContent=bytes(task.downloadedBytes)+' / '+(task.totalBytes?bytes(task.totalBytes):'—');
    progress.append(statusPill(task),taskProgress(task),size);
    const actions=document.createElement('div'); actions.className='task-actions';
    const add=(label,action)=>{const b=button(label,async()=>{b.disabled=true;try{
      if(action==='remove'&&!confirm('取消并移除任务？已下载文件保留。'))return;
      if(action==='details'){const d=await api('/api/downloads/'+task.id+'/details');$('detailsTitle').textContent=task.name;$('detailsBody').textContent=JSON.stringify({任务:d.task,引擎状态:d.engine},null,2);$('detailsDialog').showModal();return;}
      if(action==='file'||action==='files'){for(let i=0;i<(action==='files'?task.files.length:1);i++){const ticket=await api('/api/downloads/'+task.id+'/file-ticket/'+i,{method:'POST'}),a=document.createElement('a');a.href='/api/downloads/file-ticket/'+encodeURIComponent(ticket.ticket);a.download=task.files[i].split(/[\\/]/).pop();a.click();}return;}
      if(action==='open-folder'){await api('/api/downloads/'+task.id+'/open-folder',{method:'POST'});return;}
      if(action==='open-file'){await api('/api/downloads/'+task.id+'/open-file',{method:'POST'});return;}
      await api('/api/downloads/'+task.id+'/'+action,{method:'POST'});await refresh();
    }catch(e){notify(e.message);}finally{b.disabled=false;}});b.dataset.focus=task.id+':'+action;actions.append(b);};
    if(['downloading','queued','fetching_metadata','finalizing'].includes(task.status))add('暂停','pause');
    if(['paused','error'].includes(task.status))add(task.status==='error'?'重试':'继续','resume');
    if(task.status==='completed'&&!task.indexed)add('加入媒体库','index');
    add('详情','details');if(task.status==='completed'&&task.files?.length)add('保存到本机','file');if(task.status==='completed'&&task.files?.length>1)add('保存全部','files');if(task.status==='completed'&&task.files?.length&&localClient)add('打开文件','open-file');if(task.status!=='removed'&&localClient)add('打开服务器目录','open-folder');
    add('移除任务','remove');row.append(select,content,progress,actions);$('taskList').append(row);
  }
  $('selectAllTasks').checked=list.length>0&&list.every(t=>selectedIds.has(t.id));
  if(focus)document.querySelector(`[data-focus="${CSS.escape(focus)}"]`)?.focus({preventScroll:true});
}
async function refresh() {
  if(busy)return;busy=true;
  try{if(!localClientLoaded){const config=await api('/api/config');localClient=!!config.localClient;localClientLoaded=true;}const result=await api('/api/downloads');tasks=result.tasks;for(const id of selectedIds)if(!tasks.some(t=>t.id===id))selectedIds.delete(id);render();$('connection').textContent=result.engineError||(!result.aria2Available||!result.hlsAvailable?'下载引擎未就绪，请重新发布完整包。':'下载服务已连接');}
  catch(e){$('connection').textContent=e.message;}
  finally{busy=false;}
}
$('newTask').onclick=async()=>{try{const config=await api('/api/config');if(!$('downloadDirectory').value)$('downloadDirectory').value=config.inbox;$('addDialog').showModal();$('downloadSource').focus();}catch(e){notify(e.message);}};
$('closeAdd').onclick=()=>$('addDialog').close();
$('addForm').onsubmit=async event=>{event.preventDefault();$('submitTask').disabled=true;$('addError').textContent='';try{
  const file=$('torrentFile').files[0], directory=$('downloadDirectory').value, name=$('downloadName').value, autoIndex=$('autoIndex').checked;
  if(file&&$('downloadSource').value.trim())throw Error('请只填写链接或选择种子文件。');
  if(file){if(file.size>8*1024*1024)throw Error('种子文件最大为 8 MB。');await api('/api/downloads/torrent?'+new URLSearchParams({directory,name:name||file.name,autoIndex}),{method:'POST',headers:{'Content-Type':'application/octet-stream'},body:file});}
  else {const headers={};for(const line of $('downloadHeaders').value.split(/\r?\n/)){if(!line.trim())continue;const colon=line.indexOf(':');if(colon<1)throw Error('请求头请使用 Name: Value 格式。');headers[line.slice(0,colon).trim()]=line.slice(colon+1).trim();}const request={source:$('downloadSource').value.trim(),directory,name,autoIndex,kind:$('downloadKind').value,priority:Number($('downloadPriority').value),proxy:$('downloadProxy').value.trim()||null,userAgent:$('downloadUserAgent').value.trim()||null,headers};await api('/api/downloads',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(request)});}
  $('addDialog').close();$('downloadSource').value='';$('torrentFile').value='';$('downloadName').value='';await refresh();notify('下载任务已添加');
}catch(e){$('addError').textContent=e.message;}finally{$('submitTask').disabled=false;}};
$('loginForm').onsubmit=async event=>{event.preventDefault();localStorage.setItem('mh_api_key',$('downloadKey').value.trim());try{await api('/api/config');$('downloadKey').value='';$('loginDialog').close();await refresh();}catch(e){$('loginError').textContent=e.message;}};
document.querySelectorAll('[data-tab]').forEach(b=>b.onclick=()=>{tab=b.dataset.tab;document.querySelectorAll('[data-tab]').forEach(x=>x.setAttribute('aria-pressed',String(x===b)));render();});
$('searchTasks').oninput=render;$('refreshTasks').onclick=refresh;
$('sortTasks').onchange=render;
$('threadSettings').onclick=async()=>{try{const settings=await api('/api/downloads/settings');for(const id of ['maxConcurrentTasks','connectionsPerFile','hlsThreads'])$(id).value=settings[id];$('maxDownloadSpeed').value=settings.maxDownloadSpeed||0;$('scheduledSpeedLimit').value=settings.scheduledSpeedLimit||0;$('globalProxy').value=settings.proxy||'';$('scheduleStart').value=settings.scheduleStart||'';$('scheduleEnd').value=settings.scheduleEnd||'';$('threadsError').textContent='';$('threadsDialog').showModal();}catch(e){notify(e.message);}};
$('closeThreads').onclick=()=>$('threadsDialog').close();
$('threadsForm').onsubmit=async event=>{event.preventDefault();$('saveThreads').disabled=true;$('threadsError').textContent='';try{const settings={maxConcurrentTasks:Number($('maxConcurrentTasks').value),connectionsPerFile:Number($('connectionsPerFile').value),hlsThreads:Number($('hlsThreads').value),maxDownloadSpeed:Number($('maxDownloadSpeed').value),scheduledSpeedLimit:Number($('scheduledSpeedLimit').value),proxy:$('globalProxy').value.trim()||null,scheduleStart:$('scheduleStart').value||null,scheduleEnd:$('scheduleEnd').value||null};await api('/api/downloads/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(settings)});$('threadsDialog').close();notify('下载设置已保存');await refresh();}catch(e){$('threadsError').textContent=e.message;}finally{$('saveThreads').disabled=false;}};
$('closeDetails').onclick=()=>$('detailsDialog').close();
const selected=()=>[...selectedIds];
document.addEventListener('change',e=>{if(e.target.id==='selectAllTasks')document.querySelectorAll('.task-select').forEach(x=>{x.checked=e.target.checked;if(e.target.checked)selectedIds.add(x.value);else selectedIds.delete(x.value);});else if(e.target.classList.contains('task-select')){if(e.target.checked)selectedIds.add(e.target.value);else selectedIds.delete(e.target.value);}});
for(const [id,action] of [['bulkPause','pause'],['bulkResume','resume'],['bulkRemove','remove']])$(id).onclick=async()=>{const ids=selected();if(!ids.length)return notify('请先选择任务');if(action==='remove'&&!confirm('移除所选任务？已下载文件保留。'))return;try{await api('/api/downloads/bulk',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ids,action})});await refresh();notify('批量操作完成');}catch(e){notify(e.message);}};
$('bulkPriority').onchange=async()=>{const ids=selected();if(!ids.length)return notify('请先选择任务');try{await api('/api/downloads/bulk',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ids,action:'priority',priority:Number($('bulkPriority').value)})});notify('任务优先级已更新');}catch(e){notify(e.message);}};
$('exportTasks').onclick=async()=>{try{const ids=selected(),url='/api/downloads/export'+(ids.length?'?'+ids.map(id=>'id='+encodeURIComponent(id)).join('&'):'');const data=await api(url),blob=new Blob([JSON.stringify(data,null,2)],{type:'application/json'}),a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='mediahub-download-tasks.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}catch(e){notify(e.message);}};
$('importTasks').onchange=async()=>{try{const file=$('importTasks').files[0];if(!file)return;const tasks=JSON.parse(await file.text());await api('/api/downloads/import',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(tasks)});await refresh();notify('任务已导入');}catch(e){notify(e.message);}finally{$('importTasks').value='';}};
async function browse(path){$('directoryError').textContent='';try{const d=await api('/api/browse?path='+encodeURIComponent(path));currentDirectory=d.current;parentDirectory=d.parent;$('directoryPath').textContent=d.current||'磁盘';$('selectDirectory').disabled=!d.current;$('directoryList').replaceChildren();for(const entry of d.entries.filter(e=>e.isDirectory)){const b=button(entry.name,()=>browse(entry.path));b.className='download-user-content';$('directoryList').append(b);}if(d.hasMore)$('directoryError').textContent='仅显示前 5000 项，请进入子文件夹继续浏览。';}catch(e){$('directoryError').textContent=e.message;}}
$('chooseDirectory').onclick=()=>{$('directoryDialog').showModal();browse($('downloadDirectory').value);};$('closeDirectory').onclick=()=>$('directoryDialog').close();$('parentDirectory').onclick=()=>browse(parentDirectory);$('rootDirectory').onclick=()=>browse('');$('selectDirectory').onclick=()=>{$('downloadDirectory').value=currentDirectory;$('directoryDialog').close();};
async function poll(){if(!document.hidden)await refresh();setTimeout(poll,2000);}poll();
