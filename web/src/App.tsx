import { useEffect, useRef, useState, type FormEvent } from 'react';

type Session = { token: string; username: string; role: string };
type Factory = { id: string; name: string };
type Sku = { id: string; style: string; color: string; size: string };
type Line = { skuId: string; quantity: number };
type Draft = { factoryId: string; deliveryDate: string; lines: Line[] };
type OrderLine = Line & { id: string; style: string; color: string; size: string };
type Summary = { id: string; factoryName: string; deliveryDate: string; status: string; revision: number };
type Detail = Summary & { factoryId: string; lines: OrderLine[]; lastSubmittedVersion: number; acceptedOrderVersion: number | null };
type VersionSummary = { orderId: string; version: number; factoryName: string; deliveryDate: string; status: string; expectedRevision: number | null };
type Version = VersionSummary & { submittedRevision: number; submittedBy: string; submittedAt: string; lines: OrderLine[]; resolvedBy: string | null; resolvedAt: string | null; reason: string | null };
type Audit = { id: string; action: string; subjectId: string; occurredAt: string; resultRevision: number; reason: string | null; changes: string | null };
type Page<T> = { items: T[]; total: number; page: number; pageSize: number };
type Attempt = { key: string; path: string; method: 'POST' | 'PUT'; payload: unknown; label: string; username: string; orderId?: string; version?: number };
const names: Record<string, string> = { Draft: '草稿', Submitted: '待工厂确认', Accepted: '已接受', Rejected: '已拒绝', Pending: '待确认', Withdrawn: '已撤回',
  DraftCreated: '创建草稿', UpdateDraft: '编辑草稿', SubmitOrder: '提交版本', WithdrawSubmission: '撤回提交', AcceptVersion: '接受版本', RejectVersion: '拒绝版本' };
class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}
async function api<T>(path: string, token?: string, body?: unknown, key?: string, method?: string): Promise<T> {
  const response = await fetch(path, {
    method: method ?? (body === undefined ? 'GET' : 'POST'),
    headers: { ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(key ? { 'Idempotency-Key': key } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const result = await response.json().catch(() => null);
  if (!response.ok) throw new ApiError(response.status, result?.detail || `请求失败（${response.status}）`);
  if (result === null) throw new Error('响应内容不完整；请使用原请求确认处理结果。');
  return result as T;
}
function Lines({ lines }: { lines: OrderLine[] }) {
  return <><div className="table"><table><thead><tr><th>款式</th><th>颜色</th><th>尺码</th><th>件数</th></tr></thead><tbody>
    {lines.map(line => <tr key={line.id}><td>{line.style}</td><td>{line.color}</td><td>{line.size}</td><td>{line.quantity}</td></tr>)}
  </tbody></table></div><p>总计 {lines.reduce((sum, line) => sum + line.quantity, 0)} 件</p></>;
}

export function App() {
  // ponytail: 演示令牌与未确认请求仅留内存；刷新前须确认请求，正式接入时再设计持久恢复。
  const [session, setSession] = useState<Session | null>(null);
  const [username, setUsername] = useState('buyer'); const [password, setPassword] = useState('');
  const [factories, setFactories] = useState<Factory[]>([]); const [skus, setSkus] = useState<Sku[]>([]);
  const [draft, setDraft] = useState<Draft>({ factoryId: '', deliveryDate: '', lines: [] });
  const [editing, setEditing] = useState<Detail | null>(null); const [reason, setReason] = useState('');
  const [orders, setOrders] = useState<Page<Summary> | null>(null); const [detail, setDetail] = useState<Detail | null>(null);
  const [history, setHistory] = useState<Page<VersionSummary> | null>(null); const [audit, setAudit] = useState<Page<Audit> | null>(null);
  const [factoryVersions, setFactoryVersions] = useState<Page<VersionSummary> | null>(null);
  const [selected, setSelected] = useState<Version | null>(null); const [filter, setFilter] = useState('');
  const [page, setPage] = useState(1); const [refresh, setRefresh] = useState(0);
  const [pending, setPending] = useState<Attempt | null>(null); const [busy, setBusy] = useState(false); const inFlight = useRef(false);
  const [loading, setLoading] = useState(false); const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  const locked = busy || !!pending;
  function fail(cause: unknown) {
    setError(cause instanceof Error ? cause.message : '请求失败，请重试。');
    if (cause instanceof ApiError && cause.status === 401) {
      setSession(null); setDetail(null); setSelected(null); setOrders(null); setHistory(null); setAudit(null); setFactoryVersions(null);
      setNotice('登录已失效，请原账号重新登录；未确认操作的原请求仍保留。');
    }
  }
  function resetDraft() {
    setEditing(null); setReason(''); setDraft({ factoryId: factories[0]?.id ?? '', deliveryDate: '',
      lines: skus.slice(0, 2).map(sku => ({ skuId: sku.id, quantity: 1 })) });
  }
  useEffect(() => {
    if (!session || session.role === 'Quality') return;
    let cancelled = false; setLoading(true);
    const load = session.role === 'Buyer'
      ? Promise.all([api<Factory[]>('/api/factories', session.token), api<Sku[]>('/api/skus', session.token),
        api<Page<Summary>>(`/api/purchase-orders?page=${page}&pageSize=20`, session.token)]).then(([factoryOptions, skuOptions, result]) => {
        if (cancelled) return;
        setFactories(factoryOptions); setSkus(skuOptions); setOrders(result);
        setDraft(current => current.factoryId ? current : { factoryId: factoryOptions[0]?.id ?? '', deliveryDate: '',
          lines: skuOptions.slice(0, 2).map(sku => ({ skuId: sku.id, quantity: 1 })) });
      })
      : api<Page<VersionSummary>>(`/api/factory/order-versions?page=${page}&pageSize=20${filter ? `&status=${filter}` : ''}`, session.token)
        .then(result => { if (!cancelled) setFactoryVersions(result); });
    load.catch(cause => { if (!cancelled) fail(cause); }).finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [session, page, refresh, filter]);
  async function loadBuyer(id: string, historyPage = 1) {
    if (!session) return;
    const [order, versions, records] = await Promise.all([
      api<Detail>(`/api/purchase-orders/${id}`, session.token),
      api<Page<VersionSummary>>(`/api/purchase-orders/${id}/versions?page=${historyPage}&pageSize=20`, session.token),
      api<Page<Audit>>(`/api/purchase-orders/${id}/audit?pageSize=20`, session.token),
    ]);
    setDetail(order); setHistory(versions); setAudit(records);
  }
  async function loadVersion(id: string, version: number) {
    if (!session) return;
    const path = session.role === 'Factory' ? `/api/factory/orders/${id}/versions/${version}` : `/api/purchase-orders/${id}/versions/${version}`;
    setSelected(await api<Version>(path, session.token));
  }
  async function inspect(action: () => Promise<void>) {
    if (locked) return; setBusy(true); setError('');
    try { await action(); } catch (cause) { fail(cause); } finally { setBusy(false); }
  }
  async function perform(attempt: Attempt) {
    if (!session || session.username !== attempt.username || inFlight.current) return;
    inFlight.current = true; setPending(attempt); setBusy(true); setError(''); setNotice('');
    try {
      const result = await api<{ orderId: string }>(attempt.path, session.token, attempt.payload, attempt.key, attempt.method);
      if (typeof result?.orderId !== 'string') throw new Error('响应缺少订单编号；请使用原请求确认处理结果。');
      setPending(null); setNotice(`${attempt.label}已完成：${result.orderId}。显示的最新状态以重新查询为准。`);
      if (attempt.path.endsWith('/draft') || attempt.path === '/api/purchase-orders') resetDraft();
      setRefresh(value => value + 1);
      try {
        if (session.role === 'Buyer') await loadBuyer(result.orderId);
        if (attempt.version) await loadVersion(result.orderId, attempt.version);
      } catch (cause) { fail(cause); } // 写入已确认；后续查询失败不重新发起新写操作。
    } catch (cause) {
      fail(cause);
      if (cause instanceof ApiError && cause.status >= 400 && cause.status < 500 && cause.status !== 401) {
        setPending(null);
        if (cause.status === 409 && attempt.orderId) {
          resetDraft(); setNotice('本次操作冲突，请核对最新内容后再发起操作。');
          try { if (session.role === 'Buyer') await loadBuyer(attempt.orderId); if (attempt.version) await loadVersion(attempt.orderId, attempt.version);
            setNotice('本次操作冲突，已重新查询；核对最新内容后再发起操作。'); }
          catch (queryError) { fail(queryError); }
          setRefresh(value => value + 1);
        }
      }
    } finally { inFlight.current = false; setBusy(false); }
  }
  function write(path: string, payload: unknown, label: string, orderId?: string, version?: number, method: 'POST' | 'PUT' = 'POST') {
    if (!session || locked) return;
    void perform({ key: crypto.randomUUID(), path, payload: structuredClone(payload), method, label, username: session.username, orderId, version });
  }
  async function login(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError('');
    try { setSession(await api<Session>('/api/demo-auth/login', undefined, { username, password })); setPassword(''); setPage(1); }
    catch (cause) { fail(cause); } finally { setBusy(false); }
  }
  function saveDraft(event: FormEvent) {
    event.preventDefault();
    if (editing) write(`/api/purchase-orders/${editing.id}/draft`, { ...draft, expectedRevision: editing.revision, reason }, '保存修改', editing.id, undefined, 'PUT');
    else write('/api/purchase-orders', draft, '创建草稿');
  }
  function beginEdit() {
    if (!detail || locked) return;
    setEditing(detail); setReason(''); setDraft({ factoryId: detail.factoryId, deliveryDate: detail.deliveryDate,
      lines: detail.lines.map(line => ({ skuId: line.skuId, quantity: line.quantity })) });
  }
  function logout() {
    if (pending && !window.confirm('前一次操作可能已提交。退出会丢失原请求，建议先重试确认；仍要退出吗？')) return;
    setSession(null); setPending(null); setSelected(null); setDetail(null); setOrders(null); setFactoryVersions(null);
    setHistory(null); setAudit(null); setNotice(''); setError(''); resetDraft();
  }
  const list = session?.role === 'Buyer' ? orders : factoryVersions;
  return <main>
    <h1>SCM供应链协同系统</h1><p>1B · 采购提交版本与工厂决定</p>
    {error && <p role="alert" className="error">{error}</p>}{notice && <p role="status">{notice}</p>}
    {pending && <section><p>“{pending.label}”结果尚未确认，已保留原内容、修订号与幂等键。请先重试确认，刷新页面会丢失内存中的请求。</p>
      <p>原账号：{pending.username} · 幂等键：{pending.key}</p>
      <button disabled={busy || session?.username !== pending.username} onClick={() => perform(pending)}>使用原请求重试</button></section>}
    {!session ? <section><h2>演示登录</h2><p>账号：buyer、factory-a、factory-b、quality。使用本地演示密码。</p>
      <form onSubmit={login}><label>账号<input autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} required /></label>
        <label>密码<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required /></label>
        <button disabled={busy}>登录</button></form></section> : <>
      <p>当前身份：{session.username}（{session.role}）<button disabled={busy} onClick={logout}>退出</button></p>
      {session.role === 'Quality' && <section><p>质量业务尚未实现，此身份不能创建、查看草稿或决定接单。</p></section>}
      {session.role === 'Buyer' && <>
        <section><h2>{editing ? `编辑草稿 · Revision ${editing.revision}` : '创建草稿'}</h2>
          <form onSubmit={saveDraft}><fieldset disabled={locked || loading}>
            <label>合作工厂<select value={draft.factoryId} onChange={e => setDraft({ ...draft, factoryId: e.target.value })} required>
              <option value="">请选择</option>{factories.map(factory => <option key={factory.id} value={factory.id}>{factory.name}</option>)}
            </select></label>
            <label>交期<input type="date" value={draft.deliveryDate} onInput={e => setDraft({ ...draft, deliveryDate: e.currentTarget.value })}
              onChange={e => setDraft({ ...draft, deliveryDate: e.target.value })} required /></label>
            {draft.lines.map((line, index) => <div className="line" key={index}>
              <label>商品 {index + 1}<select value={line.skuId} required onChange={e => setDraft({ ...draft,
                lines: draft.lines.map((item, i) => i === index ? { ...item, skuId: e.target.value } : item) })}>
                {skus.map(sku => <option key={sku.id} value={sku.id}>{sku.style} / {sku.color} / {sku.size}</option>)}
              </select></label>
              <label>件数 {index + 1}<input type="number" min="1" max="2147483647" step="1" required value={line.quantity}
                onChange={e => setDraft({ ...draft, lines: draft.lines.map((item, i) => i === index ? { ...item, quantity: Number(e.target.value) } : item) })} /></label>
              <button type="button" disabled={draft.lines.length <= 1} onClick={() => setDraft({ ...draft, lines: draft.lines.filter((_, i) => i !== index) })}>删除明细 {index + 1}</button>
            </div>)}
            <button type="button" disabled={!skus.length || draft.lines.length >= 100} onClick={() => setDraft({ ...draft, lines: [...draft.lines, { skuId: skus[0].id, quantity: 1 }] })}>添加明细</button>
            {editing && <label>修改原因（可选）<input maxLength={500} value={reason} onChange={e => setReason(e.target.value)} /></label>}
            <button disabled={!draft.lines.length}>{editing ? '保存修改' : '保存草稿'}</button>
          </fieldset></form><button disabled={locked} onClick={resetDraft}>{editing ? '取消编辑 / 创建另一张订单' : '清空并开始另一张订单'}</button>
          <p>草稿不表示工厂承诺。提交时交期必须为上海当天或以后，最多 100 个明细。</p></section>
        <section><h2>采购订单</h2><button disabled={locked || loading} onClick={() => setRefresh(value => value + 1)}>刷新列表</button>
          <div className="table"><table><thead><tr><th>订单编号</th><th>工厂</th><th>交期</th><th>状态</th><th>操作</th></tr></thead><tbody>
            {orders?.items.map(order => <tr key={order.id}><td>{order.id}</td><td>{order.factoryName}</td><td>{order.deliveryDate}</td><td>{names[order.status]}</td>
              <td><button disabled={locked} onClick={() => inspect(async () => { setSelected(null); await loadBuyer(order.id); })}>查看详情</button></td></tr>)}
          </tbody></table></div></section>
        {detail && <section><h2>订单详情</h2><p>编号：{detail.id}</p><p>{detail.factoryName} · 交期 {detail.deliveryDate} · {names[detail.status]} · Revision {detail.revision}</p>
          <p>最近提交：{detail.lastSubmittedVersion ? `V${detail.lastSubmittedVersion}` : '尚未提交'} · 接受版本：{detail.acceptedOrderVersion ? `V${detail.acceptedOrderVersion}` : '尚未接受'}</p><Lines lines={detail.lines} />
          {['Draft', 'Rejected'].includes(detail.status) && <><button disabled={locked} onClick={beginEdit}>编辑这张订单</button>
            <button disabled={locked || !!editing} onClick={() => write(`/api/purchase-orders/${detail.id}/submissions`, { expectedRevision: detail.revision }, '提交订单版本', detail.id)}>提交当前内容</button></>}
          {detail.status === 'Submitted' && <><label>撤回原因<input maxLength={500} value={reason} onChange={e => setReason(e.target.value)} disabled={locked} /></label>
            <button disabled={locked || !reason.trim()} onClick={() => write(`/api/purchase-orders/${detail.id}/versions/${detail.lastSubmittedVersion}/withdraw`,
              { expectedRevision: detail.revision, reason }, '撤回提交', detail.id, detail.lastSubmittedVersion)}>撤回待确认版本</button></>}
          {detail.status === 'Accepted' && <p>已接单；生产流程尚未实现。工厂、商品、数量和交期不能直接修改。</p>}
          <h3>提交历史</h3>{history?.items.map(version => <p key={version.version}>V{version.version} · {version.factoryName} · {names[version.status]}
            <button disabled={locked} onClick={() => inspect(() => loadVersion(detail.id, version.version))}>查看 V{version.version} 快照</button></p>)}
          {!!history?.total && <p>历史第 {history.page} 页 / 共 {history.total} 个版本
            <button disabled={locked || history.page <= 1} onClick={() => inspect(() => loadBuyer(detail.id, history.page - 1))}>历史上一页</button>
            <button disabled={locked || history.page * history.pageSize >= history.total} onClick={() => inspect(() => loadBuyer(detail.id, history.page + 1))}>历史下一页</button></p>}
          <h3>操作记录（最近 20 条，共 {audit?.total ?? 0} 条）</h3>{audit?.items.map(record => <div key={record.id}><p>
            R{record.resultRevision} · {names[record.action] ?? record.action} · {record.subjectId} · {new Date(record.occurredAt).toLocaleString()}{record.reason && ` · ${record.reason}`}</p>
            {record.changes && <details><summary>变更内容</summary><pre>{record.changes}</pre></details>}</div>)}
        </section>}
      </>}
      {session.role === 'Factory' && <section><h2>本工厂提交版本</h2>
        <label>状态<select value={filter} disabled={locked} onChange={e => { setFilter(e.target.value); setPage(1); }}><option value="">全部</option>
          {['Pending', 'Accepted', 'Rejected', 'Withdrawn'].map(status => <option key={status} value={status}>{names[status]}</option>)}</select></label>
        <button disabled={locked || loading} onClick={() => setRefresh(value => value + 1)}>刷新列表</button>
        <div className="table"><table><thead><tr><th>订单编号</th><th>版本</th><th>交期</th><th>状态</th><th>操作</th></tr></thead><tbody>
          {factoryVersions?.items.map(version => <tr key={`${version.orderId}/${version.version}`}><td>{version.orderId}</td><td>V{version.version}</td><td>{version.deliveryDate}</td><td>{names[version.status]}</td>
            <td><button disabled={locked} onClick={() => inspect(async () => { setReason(''); await loadVersion(version.orderId, version.version); })}>查看版本</button></td></tr>)}
        </tbody></table></div><p>草稿不可见；每个决定必须针对这里展示的指定版本。</p></section>}
      {session.role !== 'Quality' && <p>{loading ? '加载中…' : `第 ${page} 页 · 共 ${list?.total ?? 0} 条`}
        <button disabled={locked || loading || page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
        <button disabled={locked || loading || !list || page * list.pageSize >= list.total} onClick={() => setPage(page + 1)}>下一页</button></p>}
      {selected && <section><h2>提交快照 · V{selected.version}</h2><p>订单：{selected.orderId}</p><p>{selected.factoryName} · 交期 {selected.deliveryDate} · {names[selected.status]}</p>
        <p>提交时 Revision {selected.submittedRevision} · {selected.submittedBy} · {new Date(selected.submittedAt).toLocaleString()}</p><Lines lines={selected.lines} />
        {selected.resolvedBy && <p>处理人：{selected.resolvedBy} · {selected.resolvedAt && new Date(selected.resolvedAt).toLocaleString()} · {selected.reason ?? '已确认接受'}</p>}
        {session.role === 'Factory' && selected.expectedRevision !== null && selected.status === 'Pending' && <>
          <p>决定使用 Revision {selected.expectedRevision}。请确认以上交期和每行数量后接单。</p>
          <button disabled={locked} onClick={() => write(`/api/purchase-orders/${selected.orderId}/versions/${selected.version}/accept`,
            { expectedRevision: selected.expectedRevision }, '接受版本', selected.orderId, selected.version)}>接受 V{selected.version}</button>
          <label>拒绝原因<input maxLength={500} value={reason} disabled={locked} onChange={e => setReason(e.target.value)} /></label>
          <button disabled={locked || !reason.trim()} onClick={() => write(`/api/purchase-orders/${selected.orderId}/versions/${selected.version}/reject`,
            { expectedRevision: selected.expectedRevision, reason }, '拒绝版本', selected.orderId, selected.version)}>拒绝 V{selected.version}</button>
        </>}{selected.status === 'Accepted' && <p>已接单；生产流程尚未实现。</p>}
      </section>}
    </>}
  </main>;
}
