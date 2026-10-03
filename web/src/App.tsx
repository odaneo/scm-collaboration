import { useEffect, useState, type FormEvent } from 'react';

type Session = { token: string; username: string; role: string; factoryId: string | null };
type Factory = { id: string; name: string };
type Sku = { id: string; style: string; color: string; size: string };
type Line = { skuId: string; quantity: number };
type Draft = { factoryId: string; deliveryDate: string; lines: Line[] };
type Summary = { id: string; factoryName: string; deliveryDate: string; status: string; revision: number };
type Page = { items: Summary[]; total: number; page: number; pageSize: number };
type Detail = Summary & { createdBy: string; createdAt: string; lines: (Line & Sku & { id: string })[] };
class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}
async function api<T>(path: string, token?: string, body?: unknown, key?: string): Promise<T> {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(key ? { 'Idempotency-Key': key } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const result = await response.json().catch(() => null);
  if (!response.ok) {
    const errors = result?.errors ? Object.values(result.errors).flat().join('；') : '';
    throw new ApiError(response.status, result?.detail || errors || `请求失败（${response.status}）`);
  }
  return result as T;
}

export function App() {
  // ponytail: 演示令牌只留内存，刷新需重新登录；正式身份管理时再决定 Cookie/BFF 方案。
  const [session, setSession] = useState<Session | null>(null);
  const [username, setUsername] = useState('buyer');
  const [password, setPassword] = useState('');
  const [factories, setFactories] = useState<Factory[]>([]);
  const [skus, setSkus] = useState<Sku[]>([]);
  const [draft, setDraft] = useState<Draft>({ factoryId: '', deliveryDate: '', lines: [] });
  const [orders, setOrders] = useState<Page | null>(null);
  const [page, setPage] = useState(1);
  const [refresh, setRefresh] = useState(0);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [pending, setPending] = useState<{ key: string; payload: Draft } | null>(null);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  function fail(cause: unknown) {
    setError(cause instanceof Error ? cause.message : '请求失败，请重试。');
    if (cause instanceof ApiError && cause.status === 401) {
      setSession(null); setDetail(null); setOrders(null); setNotice('登录已失效，请重新登录。');
    }
  }
  useEffect(() => {
    if (!session || session.role !== 'Buyer') return;
    let cancelled = false;
    setLoading(true); setError('');
    Promise.all([
      api<Factory[]>('/api/factories', session.token), api<Sku[]>('/api/skus', session.token),
      api<Page>(`/api/purchase-orders?page=${page}&pageSize=20`, session.token),
    ]).then(([factoryOptions, skuOptions, result]) => {
      if (cancelled) return;
      setFactories(factoryOptions); setSkus(skuOptions); setOrders(result);
      setDraft(current => current.factoryId ? current : {
        factoryId: factoryOptions[0]?.id ?? '', deliveryDate: '',
        lines: skuOptions.slice(0, 2).map(sku => ({ skuId: sku.id, quantity: 1 })),
      });
    }).catch(cause => { if (!cancelled) fail(cause); }).finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [session, page, refresh]);

  async function login(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError('');
    try {
      setSession(await api<Session>('/api/demo-auth/login', undefined, { username, password }));
      setPassword(''); setPage(1); setNotice('');
    } catch (cause) { fail(cause); } finally { setBusy(false); }
  }
  async function submit(event: FormEvent) {
    event.preventDefault(); if (!session) return;
    const attempt = pending ?? { key: crypto.randomUUID(), payload: structuredClone(draft) };
    setPending(attempt); setBusy(true); setError(''); setNotice('');
    try {
      const created = await api<{ orderId: string }>('/api/purchase-orders', session.token, attempt.payload, attempt.key);
      setPending(null); setNotice(`草稿已保存：${created.orderId}`);
      setDraft({ factoryId: '', deliveryDate: '', lines: [] });
      setPage(1); setRefresh(value => value + 1);
      setDetail(await api<Detail>(`/api/purchase-orders/${created.orderId}`, session.token));
    } catch (cause) {
      fail(cause);
      // 明确的 4xx 没有成功创建；网络或服务失败保留键及冻结输入，允许安全重试。
      if (cause instanceof ApiError && cause.status >= 400 && cause.status < 500) setPending(null);
    } finally { setBusy(false); }
  }
  async function showOrder(id: string) {
    if (!session) return;
    setBusy(true); setError('');
    try { setDetail(await api<Detail>(`/api/purchase-orders/${id}`, session.token)); }
    catch (cause) { fail(cause); } finally { setBusy(false); }
  }
  function newDraft() {
    if (pending && !window.confirm('前一次请求可能已保存。建议先用原幂等键重试确认；仍要开始另一张订单吗？')) return;
    setPending(null); setDetail(null); setNotice(''); setError('');
    setDraft({ factoryId: factories[0]?.id ?? '', deliveryDate: '',
      lines: skus.slice(0, 2).map(sku => ({ skuId: sku.id, quantity: 1 })) });
  }
  return <main>
    <h1>SCM供应链协同系统</h1><p>1A · 采购订单草稿 · 尚未提交给工厂</p>
    {error && <p role="alert" className="error">{error}</p>}
    {notice && <p role="status">{notice}</p>}
    {!session ? <section>
      <h2>演示登录</h2><p>账号：buyer、quality、factory-a、factory-b。密码查看本地 .env 的 DEMO_PASSWORD。</p>
      <form onSubmit={login}>
        <label>账号<input autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} required /></label>
        <label>密码<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required /></label>
        <button disabled={busy}>{busy ? '登录中…' : '登录'}</button>
      </form>
    </section> : <>
      <p>当前身份：{session.username}（{session.role}） <button onClick={() => {
        setSession(null); setPending(null); setDraft({ factoryId: '', deliveryDate: '', lines: [] });
        setOrders(null); setDetail(null); setNotice(''); setError('');
      }} disabled={busy}>退出</button></p>
      {session.role !== 'Buyer' ? <section><p>本阶段草稿仅对品牌采购开放。工厂接单及质量业务在后续阶段开放。</p>
        <button onClick={async () => {
          try { await api('/api/purchase-orders', session.token); } catch (cause) { fail(cause); }
        }}>验证草稿访问权限</button></section> : <>
        {loading && <p role="status">正在加载…</p>}
        <section><h2>创建草稿</h2>
          <form onSubmit={submit}>
            <fieldset disabled={busy || !!pending || loading}>
              <label>合作工厂<select value={draft.factoryId} onChange={e => setDraft({ ...draft, factoryId: e.target.value })} required>
                <option value="">请选择</option>{factories.map(factory => <option key={factory.id} value={factory.id}>{factory.name}</option>)}
              </select></label>
              <label>交期<input type="date" value={draft.deliveryDate}
                onInput={e => setDraft({ ...draft, deliveryDate: e.currentTarget.value })}
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
            </fieldset>
            {pending && <p>处理结果尚未确认，输入已冻结。重试使用原幂等键：{pending.key}</p>}
            <button disabled={busy || loading || !draft.lines.length}>{busy ? '处理中…' : pending ? '使用原请求重试' : '保存草稿'}</button>
            <button type="button" disabled={busy} onClick={newDraft}>开始另一张订单</button>
          </form>
        </section>
        <section><h2>草稿列表</h2><button disabled={loading} onClick={() => setRefresh(value => value + 1)}>刷新列表</button>
          <div className="table"><table><thead><tr><th>订单编号</th><th>工厂</th><th>交期</th><th>状态</th><th>操作</th></tr></thead>
            <tbody>{orders?.items.map(order => <tr key={order.id}><td>{order.id}</td><td>{order.factoryName}</td><td>{order.deliveryDate}</td><td>草稿</td>
              <td><button disabled={busy} onClick={() => showOrder(order.id)}>查看详情</button></td></tr>)}</tbody></table></div>
          {!loading && orders?.total === 0 && <p>尚无订单草稿。</p>}
          <p>第 {page} 页 · 共 {orders?.total ?? 0} 条 <button disabled={page <= 1 || loading} onClick={() => setPage(page - 1)}>上一页</button>
            <button disabled={!orders || page * orders.pageSize >= orders.total || loading} onClick={() => setPage(page + 1)}>下一页</button></p>
        </section>
        {detail && <section><h2>订单详情</h2><p>编号：{detail.id}</p><p>{detail.factoryName} · 交期 {detail.deliveryDate} · 草稿 · 修订 {detail.revision}</p>
          <p>创建人：{detail.createdBy} · {new Date(detail.createdAt).toLocaleString()}</p>
          <table><thead><tr><th>款式</th><th>颜色</th><th>尺码</th><th>件数</th></tr></thead><tbody>
            {detail.lines.map(line => <tr key={line.id}><td>{line.style}</td><td>{line.color}</td><td>{line.size}</td><td>{line.quantity}</td></tr>)}
          </tbody></table><p>总计 {detail.lines.reduce((sum, line) => sum + line.quantity, 0)} 件。保存草稿尚未形成工厂接单承诺。</p>
        </section>}
      </>}
    </>}
  </main>;
}
