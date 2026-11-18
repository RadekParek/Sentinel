/* Shared domain logic. Credentials and HTTP authorization remain in native code. */
(() => {
  'use strict';
  const pending = new Map(); let sequence = 0, refreshFlight = null;
  window.sentinelReceive = response => {
    if (typeof response === 'string') { try { response = JSON.parse(response); } catch { return; } }
    const item = pending.get(response?.id); if (!item) return;
    pending.delete(response.id); clearTimeout(item.timeout);
    response.error ? item.reject(new Error(response.error)) : item.resolve(response.result);
  };
  window.chrome?.webview?.addEventListener('message', event => window.sentinelReceive(event.data));
  const isNative = !!(window.chrome?.webview || window.SentinelNative);
  function native(method, params = {}) {
    if (window.SENTINEL_TEST_NATIVE) return window.SENTINEL_TEST_NATIVE(method, params);
    if (!isNative) return Promise.reject(new Error('Open Sentinel.exe or the Android app to use connections and the vault.'));
    return new Promise((resolve,reject) => {
      const id = String(++sequence);
      const timeout = setTimeout(() => { pending.delete(id); reject(new Error('Operation timed out. Try refreshing again.')); }, method === 'signInOpenRouter' ? 330000 : method === 'scanLocal' ? 180000 : 150000);
      pending.set(id, {resolve,reject,timeout});
      const request = {id,method,params};
      try { window.chrome?.webview ? window.chrome.webview.postMessage(request) : window.SentinelNative.postMessage(JSON.stringify(request)); }
      catch { pending.delete(id); clearTimeout(timeout); reject(new Error('Native connection is unavailable.')); }
    });
  }
  const number = value => (typeof value==='number'||typeof value==='string'&&value.trim()!=='') && Number.isFinite(Number(value)) ? Number(value) : null;
  const n = value => number(value) ?? 0;
  const day = timestamp => new Date(timestamp).toISOString().slice(0,10);
  const stamp = () => new Date().toISOString();
  async function request(connection, path, query, body) {
    const suffix = query ? '?' + new URLSearchParams(query) : '';
    return native('request', {id:connection.id,path:path+suffix,...(body ? {body} : {})});
  }
  async function pages(connection,path,query) {
    const buckets = []; let cursor = null; const seen = new Set();
    for (let index=0; index<100; index++) {
      const response = await request(connection,path,{...query,...(cursor?{page:cursor}:{})});
      if (!Array.isArray(response.data)) throw new Error('Provider returned an unexpected report format.');
      buckets.push(...response.data);
      if (!response.has_more) return buckets;
      if (!response.next_page || seen.has(response.next_page)) throw new Error('Provider returned an invalid pagination cursor.');
      cursor=response.next_page; seen.add(cursor);
    }
    throw new Error('Report is too large to finish; no partial total was saved.');
  }
  async function cloudReport(connection) {
    const openai = connection.adapter === 'openai';
    const end = new Date(); const start = new Date(end); start.setUTCDate(start.getUTCDate()-29); start.setUTCHours(0,0,0,0);
    const query = openai ? {start_time:Math.floor(start/1000),end_time:Math.floor(end/1000),bucket_width:'1d',limit:31} : {starting_at:start.toISOString(),ending_at:end.toISOString(),bucket_width:'1d',limit:31};
    const results = await Promise.allSettled([
      pages(connection, openai?'/v1/organization/usage/completions':'/v1/organizations/usage_report/messages', query),
      pages(connection, openai?'/v1/organization/costs':'/v1/organizations/cost_report', query)
    ]);
    if(results.every(x=>x.status==='rejected')) throw results[0].reason;
    const history = new Map(); const output={status:'ok',source:'Organization API · last 30 days',detail:openai?'Organization API activity. ChatGPT and Codex subscription billing are separate.':'Console API activity. Claude subscription billing is separate; legacy cost reports omit Priority Tier.'};
    function bucket(date){if(!history.has(date)) history.set(date,{date}); return history.get(date);}
    if(results[0].status==='fulfilled') {
      output.inputTokens=0;output.outputTokens=0;output.cachedTokens=0;output.requests=0;let requestsKnown=true;
      for(const b of results[0].value) {
        const date=day(openai?b.start_time*1000:b.starting_at); const h=bucket(date);h.tokens=h.tokens??0;
        if(!Array.isArray(b.results))throw new Error('Provider returned an incomplete token bucket.');
        for(const r of b.results) {
          if(number(r.output_tokens)===null||openai&&number(r.input_tokens)===null||!openai&&number(r.uncached_input_tokens)===null)throw new Error('Provider returned an incomplete token count.');
          const input=openai?n(r.input_tokens):n(r.uncached_input_tokens)+n(r.cache_read_input_tokens)+n(r.cache_creation?.ephemeral_5m_input_tokens)+n(r.cache_creation?.ephemeral_1h_input_tokens);
          const out=n(r.output_tokens),cached=openai?n(r.input_cached_tokens):n(r.cache_read_input_tokens);
          const requests=number(r.num_model_requests??r.requests);if(requests===null)requestsKnown=false;
          output.inputTokens+=input;output.outputTokens+=out;output.cachedTokens+=cached;output.requests+=n(requests);h.tokens+=input+out;
        }
      }
      output.tokens=output.inputTokens+output.outputTokens;
      if(!requestsKnown)delete output.requests;
    } else output.detail+=' Token report unavailable: '+results[0].reason.message;
    if(results[1].status==='fulfilled') {
      output.usageUsd=0;
      for(const b of results[1].value) {
        const h=bucket(day(openai?b.start_time*1000:b.starting_at));h.usageUsd=h.usageUsd??0;
        if(!Array.isArray(b.results))throw new Error('Provider returned an incomplete cost bucket.');
        for(const r of b.results) {
          const value=number(openai?r.amount?.value:r.amount);
          if(value===null) throw new Error('Provider returned an unexpected cost amount.');
          if(openai&&r.amount?.currency&&r.amount.currency.toLowerCase()!=='usd') throw new Error('Provider report currency is not USD.');
          const dollars=openai?value:value/100;output.usageUsd+=dollars;h.usageUsd+=dollars;
        }
      }
    } else output.detail+=' Cost report unavailable: '+results[1].reason.message;
    output.history=[...history.values()].sort((a,b)=>a.date.localeCompare(b.date));return output;
  }
  async function refreshConnection(c, previous={}) {
    const checkedAt=stamp();
    try {
      if(c.adapter==='manual') return {status:'manual',checkedAt,source:'Manually entered',detail:'This service has no automatic usage connector in Sentinel. Open its dashboard for authoritative usage.',balanceUsd:number(c.manualBalance),manualUpdatedAt:c.manualUpdatedAt};
      if(c.adapter!=='ollama'&&!c.hasKey) return {status:'unconfigured',checkedAt,source:'Awaiting API key',detail:'Add a key to retrieve real provider data.'};
      let result;
      if(c.adapter==='openai'||c.adapter==='anthropic') result=await cloudReport(c);
      if(c.adapter==='openrouter') {
        const info=(await request(c,'/api/v1/key')).data;
        if(!info||number(info.usage)===null) throw new Error('Provider returned an unexpected key report.');
        result={status:'ok',source:'OpenRouter API · key and account',usageUsd:number(info.usage_monthly),usagePeriod:'Calendar month · this key',keyRemainingUsd:number(info.limit_remaining),detail:'Spend covers this key in the current calendar month. Key allowance differs from the account wallet.'};
        try { const credits=(await request(c,'/api/v1/credits')).data;
          if(number(credits?.total_credits)!==null&&number(credits?.total_usage)!==null) result.balanceUsd=n(credits.total_credits)-n(credits.total_usage);
          else result.detail+=' Account wallet format was not recognized.';
        } catch { result.detail+=' Account wallet is unavailable to this key; its usage still refreshed.'; }
      }
      if(c.adapter==='deepseek') {
        const data=await request(c,'/user/balance');
        if(!Array.isArray(data.balance_infos)) throw new Error('Provider returned an unexpected balance report.');
        const usd=data.balance_infos.find(x=>x.currency==='USD');
        result={status:'ok',source:'DeepSeek balance API',balanceUsd:usd?number(usd.total_balance):null,detail:'Provider-reported prepaid balance. Historical usage is available in the DeepSeek console.',balances:data.balance_infos.map(x=>({currency:x.currency,amount:number(x.total_balance)}))};
      }
      if(c.adapter==='coarena') {
        const credits=await request(c,'/api/v1/credits');
        if(number(credits.balance_usd)===null) throw new Error('Provider returned an unexpected credits report.');
        result={status:'ok',source:'Coarena Model API',balanceUsd:number(credits.available_usd??credits.balance_usd),detail:'Available credits after in-flight holds, reported by Coarena.'};
        try { const response=await request(c,'/api/v1/usage',{range:'30d'}); const u=response.data;
          if(!u?.totals) throw new Error('Unexpected usage format.');
          result.inputTokens=number(u.totals.input_tokens);result.outputTokens=number(u.totals.output_tokens);result.cachedTokens=number(u.totals.cached_tokens);if(result.inputTokens!==null&&result.outputTokens!==null)result.tokens=result.inputTokens+result.outputTokens;result.requests=number(u.totals.requests);result.usageUsd=number(u.totals.spent_usd);result.usagePeriod='Last 30 days';
          result.detail+=' Account usage covers the last 30 days.';
        } catch {result.detail+=' Usage needs an inference:read key; credits can also use an inference:write key.';}
      }
      if(c.adapter==='agentrouter') {
        const info=await request(c,'/v1/dashboard/billing/subscription');
        if(number(info.hard_limit_usd)===null)throw new Error('AgentRouter returned an unexpected API-key allowance report.');
        result={status:'ok',source:'AgentRouter API-key compatibility report',keyLimitUsd:number(info.hard_limit_usd),detail:'The provider reports this API-key allowance as hard_limit_usd. It is kept separate from the account wallet. Promotional or key quotas are not cash balances.'};
        try{const usage=await request(c,'/v1/dashboard/billing/usage');if(number(usage.total_usage)!==null){result.reportedUsageRaw=number(usage.total_usage);result.detail+=' The raw total_usage counter has no verified unit or date range in AgentRouter documentation; it is not converted to dollars.';}}catch{result.detail+=' The raw usage counter could not be refreshed.';}
      }
      if(c.adapter==='ollama') {
        const started=Date.now();const tags=await request(c,'/api/tags');
        if(!Array.isArray(tags.models)) throw new Error('Ollama returned an unexpected model list.');
        let loaded=[];try {loaded=(await request(c,'/api/ps')).models||[];}catch{}
        result={status:'ok',source:'Ollama server · measured by Sentinel',models:tags.models.map(x=>({name:x.name,size:x.size,loaded:loaded.some(m=>m.name===x.name)})),latencyMs:Date.now()-started,detail:'Installed and loaded models are live. Token totals cover only benchmark requests made in Sentinel; Ollama exposes no system-wide usage ledger.'};
        for(const k of ['tokens','inputTokens','outputTokens','requests','history','tokensPerSecond']) if(previous[k]!==undefined) result[k]=previous[k];
        try{const meter=await native('getOllamaUsage',{id:c.id});if(meter.available){for(const k of ['tokens','inputTokens','outputTokens','requests','history'])result[k]=meter[k];result.meterEnabled=meter.enabled;result.source='Ollama server + local usage meter';result.detail='Last 30 days of real requests through the Sentinel proxy (127.0.0.1:11435), plus Sentinel benchmarks. Configure other clients to use this proxy while Sentinel is open. Requests sent directly to Ollama are outside this total.';}}catch{}
      }
      if(!result) throw new Error('Unknown connector.');
      return {...result,checkedAt};
    } catch(error) {
      return {status:c.adapter==='ollama'?'offline':'error',checkedAt,source:'Refresh failed',detail:error.message,lastGoodAt:previous.status==='ok'?previous.checkedAt:previous.lastGoodAt};
    }
  }
  async function refresh() {
    const state=await native('getState');
    const localTask=native('scanLocal').catch(()=>state.localUsage);
    const snapshots={};
    // Bound concurrency to avoid exhausting provider rate limits or the phone's sockets.
    let next=0;await Promise.all(Array.from({length:Math.min(3,state.connections.length)},async()=>{
      while(next<state.connections.length){const c=state.connections[next++]; snapshots[c.id]=await refreshConnection(c,state.snapshots[c.id]);}
    }));
    return native('saveSnapshots',{snapshots,localUsage:await localTask,refreshedAt:stamp()});
  }
  async function call(method,params={}) {
    if(method==='refresh') {if(!refreshFlight) refreshFlight=refresh().finally(()=>refreshFlight=null);return refreshFlight;}
    if(refreshFlight&&['saveConnection','removeConnection','saveSettings','benchmarkOllama','clearKey','clearActivityLog','importLocalConnections','signInOpenRouter','finishSignInOpenRouter'].includes(method)) await refreshFlight;
    if(method==='benchmarkOllama') {
      const state=await native('getState');const c=state.connections.find(x=>x.id===params.id&&x.adapter==='ollama');if(!c)throw new Error('Ollama connection not found.');
      const response=await request(c,'/api/generate',null,{model:params.model,prompt:'Reply with one short sentence about the night sky.',stream:false,options:{num_predict:32}});
      if(number(response.prompt_eval_count)===null||number(response.eval_count)===null) throw new Error('Ollama did not return token measurements.');
      const s=state.snapshots[c.id]||{};const input=n(response.prompt_eval_count),output=n(response.eval_count),today=day(Date.now());
      const history=(s.history||[]).filter(x=>x.date>=day(Date.now()-30*86400000));const h=history.find(x=>x.date===today);if(h)h.tokens+=input+output;else history.push({date:today,tokens:input+output});
      state.snapshots[c.id]={...s,status:'ok',source:'Ollama server · measured by Sentinel',detail:'Real token measurements from benchmarks run in Sentinel. Other apps using Ollama are outside this total.',inputTokens:n(s.inputTokens)+input,outputTokens:n(s.outputTokens)+output,tokens:n(s.tokens)+input+output,requests:n(s.requests)+1,history,checkedAt:stamp(),tokensPerSecond:response.eval_duration?output/(response.eval_duration/1e9):null};
      try{const meter=await native('getOllamaUsage',{id:c.id});if(meter.available){for(const k of ['tokens','inputTokens','outputTokens','requests','history'])state.snapshots[c.id][k]=meter[k];state.snapshots[c.id].detail='Real requests through the local usage meter, plus Sentinel benchmarks, in the last 30 days.';}}catch{}
      return native('saveSnapshots',{snapshots:state.snapshots,localUsage:state.localUsage,refreshedAt:stamp()});
    }
    return native(method,params);
  }
  window.Sentinel={call,isNative,refreshConnection,cloudReport};
})();
