/* Content activity dates and calendar groups. No note content is persisted. */
((root) => {
  'use strict';
  const modes = ['week','month','year'];
  const validDate = value => {
    if (!value) return null;
    const date = new Date(value);
    return Number.isFinite(date.getTime()) ? date : null;
  };
  function modified(note, deleted = false) {
    const raw = note.raw || note;
    const values = deleted ? [raw.deletedAt] : [raw.document?.contentUpdatedAt,raw.contentUpdatedAt];
    for (const value of [...values,raw.updatedAt,note.date && `${note.date}T${note.time || '12:00'}`,raw.createdAt]) {
      const date = validDate(value); if (date) return date;
    }
    return null;
  }
  const created = note => validDate((note.raw || note).createdAt);
  const stamp = (note, deleted) => modified(note,deleted)?.getTime() ?? -Infinity;
  function compare(a,b,deleted = false) {
    const first = stamp(a,deleted), second = stamp(b,deleted);
    return (first === second ? 0 : first > second ? -1 : 1) || String(a.id).localeCompare(String(b.id));
  }
  const pad = value => String(value).padStart(2,'0');
  const dayKey = date => `${date.getFullYear()}-${pad(date.getMonth()+1)}-${pad(date.getDate())}`;
  function calendar(date,mode) {
    if (!date) return {key:'unknown',label:'日期未记录'};
    const year = date.getFullYear(), month = date.getMonth()+1;
    if (mode === 'year') return {key:String(year),label:`${year}年`};
    if (mode !== 'week') return {key:`${year}-${pad(month)}`,label:`${year}年${month}月`};
    // Use local calendar days at noon, including across daylight-saving changes.
    const start = new Date(year,month-1,date.getDate(),12);
    start.setDate(start.getDate()-(start.getDay()+6)%7);
    const end = new Date(start); end.setDate(end.getDate()+6);
    const startLabel = `${start.getFullYear()}年${start.getMonth()+1}月${start.getDate()}日`;
    const endLabel = `${start.getFullYear() === end.getFullYear() ? '' : `${end.getFullYear()}年`}${end.getMonth()+1}月${end.getDate()}日`;
    return {key:dayKey(start),label:`${startLabel} – ${endLabel}`};
  }
  function groups(notes,mode = 'month',deleted = false) {
    const result = new Map();
    for (const note of [...notes].sort((a,b) => compare(a,b,deleted))) {
      const group = calendar(modified(note,deleted),mode);
      if (!result.has(group.key)) result.set(group.key,{...group,notes:[]});
      result.get(group.key).notes.push(note);
    }
    return [...result.values()];
  }
  function heading(group,mode,now = new Date()) {
    if (group.key === 'unknown') return {title:'日期未记录',detail:''};
    if (mode === 'year') return {title:`${group.key}年`,detail:''};
    const [year,month] = group.key.split('-');
    if (mode !== 'week') return {title:`${Number(month)}月`,detail:year};
    const current = calendar(now,'week').key;
    const previous = new Date(now); previous.setDate(previous.getDate()-7);
    const start = new Date(`${group.key}T12:00:00`);
    const title = group.key === current ? '本周' : group.key === calendar(previous,'week').key ? '上周' : `${start.getMonth()+1}月${start.getDate()}日`;
    return {title,detail:year};
  }
  const absolute = date => date ? date.toLocaleString('zh-CN',{year:'numeric',month:'long',day:'numeric',hour:'2-digit',minute:'2-digit',hour12:false}) : '时间未记录';
  function label(note,now = new Date(),deleted = false) {
    const date = modified(note,deleted); if (!date) return '日期未记录';
    const yesterday = new Date(now); yesterday.setDate(yesterday.getDate()-1);
    const day = dayKey(date) === dayKey(now) ? '今天' : dayKey(date) === dayKey(yesterday) ? '昨天' : `${date.getFullYear() === now.getFullYear() ? '' : `${date.getFullYear()}年`}${date.getMonth()+1}月${date.getDate()}日`;
    return `${day}${deleted ? '删除' : '更新'}`;
  }
  const client = {modes,modified,created,compare,calendar,groups,heading,absolute,label};
  if (typeof module !== 'undefined' && module.exports) module.exports = client;
  else root.KimNotesTime = client;
})(typeof window !== 'undefined' ? window : globalThis);
