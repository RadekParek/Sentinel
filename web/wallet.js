/* Only available credits belong in a wallet. Never add spend, limits or currencies together. */
(() => {
  const fresh = (s, now) => s?.status === 'ok' && Number.isFinite(Date.parse(s.checkedAt)) && now-Date.parse(s.checkedAt) <= 15*60*1000 && now-Date.parse(s.checkedAt) >= -60000;
  function summarize(state, now=Date.now()) {
    const candidates=[],excluded=[],manual=[];
    for(const c of state.connections||[]) {
      const s=state.snapshots?.[c.id]||{};
      if(c.adapter==='ollama')continue;
      if(c.adapter==='manual') {if(Number.isFinite(c.manualBalance))manual.push({connection:c,amount:c.manualBalance,at:c.manualUpdatedAt});else excluded.push({connection:c,reason:'No automatic wallet connector; open provider dashboard'});continue;}
      if(!Number.isFinite(s.balanceUsd)){excluded.push({connection:c,reason:s.balances?.length?'Balance reported in another currency':'No available USD balance'});continue;}
      if(!fresh(s,now)){excluded.push({connection:c,reason:'Balance needs a refresh'});continue;}
      if(s.balanceUsd<0){excluded.push({connection:c,reason:'Negative balance; inspect provider report'});continue;}
      candidates.push({connection:c,snapshot:s,amount:s.balanceUsd});
    }
    const groups=new Map();
    for(const row of candidates){const c=row.connection;const key=c.providerId+'|'+(c.walletGroup||'unconfirmed');if(!groups.has(key))groups.set(key,[]);groups.get(key).push(row);}
    const wallets=[];
    for(const rows of groups.values()){
      if(rows.length>1&&!rows[0].connection.walletGroup){for(const r of rows)excluded.push({connection:r.connection,reason:'Multiple keys: assign an account wallet group first'});continue;}
      if(rows.some(r=>Math.abs(r.amount-rows[0].amount)>.01)){for(const r of rows)excluded.push({connection:r.connection,reason:'Shared wallet reports disagree; refresh and check grouping'});continue;}
      wallets.push({...rows[0],connections:rows.map(r=>r.connection)});
    }
    const total=wallets.reduce((s,w)=>s+w.amount,0), providers=new Map();
    for(const w of wallets){const id=w.connection.providerId;const p=providers.get(id)||{providerId:id,amount:0,wallets:[]};p.amount+=w.amount;p.wallets.push(w);providers.set(id,p);}
    return {total,known:wallets.length,wallets,excluded,manual,providers:[...providers.values()].sort((a,b)=>b.amount-a.amount).map(p=>({...p,percentage:total>0?p.amount/total*100:null}))};
  }
  window.SentinelWallet={summarize,fresh};
})();
