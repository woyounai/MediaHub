// DOM adaptations of Motrix 2.4.5 (MIT), revision e17aed07.
// See THIRD-PARTY-NOTICES.txt for original files and the license.
export const activeStatuses = ['queued','fetching_metadata','metadata_ready','downloading','finalizing','seeding','paused'];
export function taskMatchesTab(task, tab) {
  switch (tab) {
    case 'all': return task.status !== 'removed';
    case 'active': return activeStatuses.includes(task.status);
    case 'completed': return task.status === 'completed';
    case 'error': return task.status === 'error';
    default: return false;
  }
}
export function taskMatchesQuery(task, needle) {
  const normalize = value => value.normalize('NFC').toLowerCase();
  const n = normalize(needle.trim());
  return !n || [task.name, task.source ?? '', task.saveDir ?? ''].map(normalize).join(' ').includes(n);
}
export function formatProgressPercent(progress, fractionDigits = 1) {
  if (!Number.isFinite(progress) || progress <= 0) return 0;
  const scale = 10 ** fractionDigits;
  return progress >= 1 ? 100 : Math.min(100 - 1 / scale, Math.round(progress * 100 * scale) / scale);
}
const labels = {queued:'排队中',fetching_metadata:'获取元数据',metadata_ready:'元数据就绪',downloading:'下载中',finalizing:'合并中',seeding:'做种中',paused:'已暂停',completed:'已完成',error:'任务失败',removed:'已取消'};
export function statusPill(task) {
  const span = document.createElement('span');
  span.className = 'status-pill';
  span.textContent = labels[task.status] || task.status;
  return span;
}
export function taskProgress(task) {
  const box = document.createElement('div'); box.className = 'task-progress';
  const bar = document.createElement('progress'); bar.max = 100;
  const label = document.createElement('span');
  if (task.progress != null) {
    bar.value = formatProgressPercent(task.progress);
    label.textContent = bar.value.toFixed(1) + '%';
  } else label.textContent = '—';
  bar.setAttribute('aria-label', task.name); box.append(bar, label); return box;
}
