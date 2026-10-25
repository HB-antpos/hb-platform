// 两周样例（2026-10-05 周一 ~ 10-18 周日；10-05 为 QLD King's Birthday 公共假期）
// 口径（规格 docs/plans/2026-10-21-attendance-payroll-leave.md）：
//   只计已审核，只算已批准的加班；计薪 = 排班内工时 + 已批加班 − 扣餐 + 已批加回；
//   周末、公假按实际工时，不乘系数，时薪表分 平日 / 周六 / 周日 / 公假 四档；
//   带薪假（病假、年假、已确认公假、年假发放）按平日时薪；
//   年假休息：每个工作日记 8 h，周末、公假不算；最终发放工时由财务按累计带薪小时核定（年假发放同样）；
//   现金时薪只有一个价，不分周末 / 公假（10-26 定）；转账时薪分四档；
//   转账+现金：转账容量 = 固定工时/周 × 周数，按 周日 → 周六 → 带薪假 → 公假 → 平日 依次装入。
const PDAYS=['10-05','10-06','10-07','10-08','10-09','10-10','10-11','10-12','10-13','10-14','10-15','10-16','10-17','10-18'];
const PWD=['一','二','三','四','五','六','日','一','二','三','四','五','六','日'];
const PH_IDX=0, PH_NAME="King's Birthday";
const mm=t=>{const[h,x]=t.split(':').map(Number);return h*60+x;};
const dec=v=>String(+(v/60).toFixed(2));          // 分钟 → 十进制小时，最多 2 位小数
const hrs=v=>String(+v.toFixed(2));
const money=v=>(v<0?'−':'')+'$'+Math.abs(v).toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g,',');
const R4=(a,b,c,d)=>({wd:a,sat:b,sun:c,ph:d});
const P=[
 {name:'Sarah Kim',role:'Manager',type:'全职',av:'SK',c:'#6941C6',limit:6,
  pay:{mode:'mix',fixedPerWeek:38,t:R4(32,40,48,72),c:32,allow:50,note:'补贴：开关店 $50'},
  days:[
   {ph:true,sch:'08:30-17:00',seg:['08:29-12:30','13:00-17:01']},
   {sch:'08:30-17:00',seg:['08:30-12:30','13:00-17:00']},
   null,
   {sch:'08:30-12:00',seg:['08:28-12:00'],other:{store:'Sunnybank',h:4}},
   {sch:'08:30-17:00',seg:['08:31-12:30','13:02-17:00']},
   null,
   {sch:'08:30-17:00',seg:['08:30-12:30','13:00-17:03']},
   {sch:'08:30-17:00',seg:['08:28-12:30','13:01-17:05']},
   {sch:'08:30-17:00',seg:['08:31-12:00','12:30-14:40','14:55-17:02']},
   null,
   {sch:'08:30-17:00',seg:['08:25-13:00','13:30-17:00']},
   {sch:'08:30-17:00',seg:['08:30-17:01']},
   null,
   {sch:'08:30-17:00',seg:['09:07-12:30','13:00-17:00'],pend:'补卡待审'},
  ]},
 {name:'Emma Walsh',role:'Staff',type:'兼职',av:'EW',c:'#1570EF',limit:4,
  // 年假申请 3 个工作日 × 8 h = 24 h；财务按累计带薪小时只核定 20 h（示例）
  pay:{mode:'transfer',t:R4(29.5,36.88,44.25,66.38),allow:0,annualApproved:20,note:'年假申请 24 h，累计只够 20 h'},
  days:[
   {ph:true,off:true},
   {leave:{t:'年',h:8}},{leave:{t:'年',h:8}},{leave:{t:'年',h:8}},
   {sch:'09:00-13:00',seg:['08:58-13:00']},
   {sch:'09:00-17:00',seg:['09:00-12:30','13:00-17:01']},
   null,
   {sch:'09:00-15:00',seg:['09:00-15:02'],ok:'补卡已批'},
   {unavail:true},
   {sch:'12:00-18:00',seg:['11:58-15:00','15:30-18:03']},
   null,
   {sch:'09:00-13:00',seg:['08:59-13:00']},
   {sch:'09:00-17:00',seg:['08:58-13:02','13:33-17:47'],pend:'加班待审'},
   null,
  ]},
 {name:'Jack Nguyen',role:'Staff',type:'全职',av:'JN',c:'#0E9384',limit:4,
  pay:{mode:'mix',fixedPerWeek:30,t:R4(28.5,35.63,42.75,64.13),c:28.5,allow:-20,note:'扣回 10-09 借支 $20'},
  days:[
   {ph:true,off:true,phPaid:{h:7.6,state:'ok',n:8}},
   {leave:{t:'病',h:8,cert:true}},
   {sch:'09:00-17:30',seg:['08:56-13:00','13:31-17:30']},
   null,
   {sch:'13:00-21:00',seg:['12:59-17:00','17:30-21:01']},
   {sch:'09:00-17:30',seg:['09:00-13:00','13:30-17:32']},
   null,
   null,
   {sch:'09:00-17:30',seg:['08:55-13:00','13:30-17:31']},
   null,
   {sch:'09:00-17:30',seg:['09:12-13:00','13:30-17:30'],pend:'迟到待审'},
   {sch:'13:00-21:00',seg:['12:58-21:02']},
   {unavail:true},
   {sch:'09:00-17:30',seg:['08:57-12:30'],auto:'13:02-00:00',pend:'补卡待审'},
  ]},
 {name:'Olivia Chen',role:'Staff',type:'兼职',av:'OC',c:'#C11574',limit:4,
  pay:{mode:'transfer',t:R4(29,36.25,43.5,65.25),allow:0,note:'年假发放 10-12~10-18，照常上班'},
  days:[
   {ph:true,off:true,phPaid:{h:6,state:'ask',n:7}},
   {sch:'10:00-16:00',seg:['10:00-16:00']},
   null,
   {sch:'09:00-15:00',seg:['08:59-15:00']},
   null,
   {sch:'10:00-16:00',seg:['10:00-16:02']},
   null,
   {sch:'10:00-16:00',seg:['09:58-16:00'],cash:6},
   null,
   {leave:{t:'病',h:6,cert:true}},
   null,
   {sch:'09:00-18:30',seg:['08:59-13:30','14:00-18:31'],pend:'加回待审',cash:6},
   {sch:'10:00-16:00',seg:['10:00-16:01'],cash:6},
   null,
  ]},
 {name:'Mia Robinson',role:'Staff',type:'临时',av:'MR',c:'#4E5BA6',limit:4,age:17,bday:11,
  pay:{mode:'cash',c:23.1,allow:0,note:'10-16 满 17 岁，核对新时薪'},
  days:[
   {ph:true,off:true},
   {sch:'09:00-15:00',seg:['09:00-15:01']},
   null,
   {sch:'15:30-18:00',seg:['15:30-18:00']},
   null,
   {sch:'09:00-17:00',seg:['08:59-12:30','13:00-17:00']},
   null,
   null,
   {sch:'09:00-18:00',seg:['09:00-18:02'],addBack:30,ok:'加回已批'},
   {sch:'09:00-15:00',seg:['09:01-15:00']},
   null,
   {sch:'12:00-18:00',seg:['11:59-18:00']},
   {sch:'09:00-17:00',seg:['08:59-12:30','13:00-17:02']},
   {sch:'10:00-16:00',seg:['10:00-16:00']},
  ]},
 {name:'Liam Patel',role:'Staff',type:'临时',av:'LP',c:'#DC6803',limit:4,
  pay:{mode:'mix',fixedPerWeek:10,t:R4(27,33.75,40.5,60.75),c:27,allow:0,note:''},
  days:[
   {ph:true,sch:'12:00-18:00',seg:['12:00-18:01']},
   null,
   {off:true,other:{store:'Sunnybank',h:5.5}},
   {sch:'16:00-21:30',seg:['16:00-21:30']},
   null,
   {sch:'12:00-20:00',seg:['11:59-16:00','16:30-20:00']},
   {sch:'12:00-18:00',seg:['12:00-18:00']},
   {sch:'16:00-21:30',seg:['16:00-21:30']},
   null,
   {sch:'16:00-21:30',seg:['15:58-22:02'],ot:30,ok:'加班已批'},
   {sch:'16:00-21:30',seg:['16:01-21:30']},
   null,
   {sch:'12:00-20:00',seg:['11:58-16:00','16:30-20:00']},
   {sch:'12:00-18:00',seg:['12:00-18:00']},
  ]},
];
function pcalc(d,i){
  if(!d||!d.sch) return null;
  const[ss,se]=d.sch.split('-').map(mm);
  const segs=d.seg.map(s=>s.split('-').map(mm));
  const worked=segs.reduce((a,[x,y])=>a+(y-x),0);
  const inSch=segs.reduce((a,[x,y])=>a+Math.max(0,Math.min(y,se)-Math.max(x,ss)),0);
  const dur=se-ss, eff=dur>540?2:dur>270?1:0, req=Math.min(eff,Math.floor(worked/240));
  let gaps=0; for(let k=1;k<segs.length;k++) if(segs[k][0]-segs[k-1][1]>=30) gaps++;
  const ded=Math.max(0,req-gaps)*30, paid=inSch+(d.ot||0)-ded+(d.addBack||0);
  return {worked,paid,ded,first:d.seg[0].split('-')[0],last:d.auto?'00:00':d.seg[d.seg.length-1].split('-')[1],n:d.seg.length+(d.auto?1:0)};
}
const ORDER=['sun','sat','leave','ph','wd'];          // 转账装填顺序（10-26 定）
const LB={leave:'假',wd:'平',sat:'六',sun:'日',ph:'公'};
function prow(e,from,to){
  const idx=[...Array(to-from).keys()].map(k=>k+from);
  const cs={}; idx.forEach(i=>cs[i]=pcalc(e.days[i],i));
  const by={wd:0,sat:0,sun:0,ph:0};
  let paid=0,pend=0,other=0,sick=0,annual=0,ph=0,cash=0;
  idx.forEach(i=>{const d=e.days[i],c=cs[i];
    if(c){paid+=c.paid; const k=i===PH_IDX?'ph':i%7===5?'sat':i%7===6?'sun':'wd'; by[k]+=c.paid;}
    if(d&&d.pend)pend++; if(d&&d.leave){d.leave.t==='病'?sick+=d.leave.h:annual+=d.leave.h;}
    if(d&&d.phPaid&&d.phPaid.state==='ok')ph+=d.phPaid.h; if(d&&d.phPaid&&d.phPaid.state==='ask')pend++;
    if(d&&d.cash)cash+=d.cash; if(d&&d.other)other+=d.other.h;});
  const r2=v=>Math.round(v*100)/100;
  const p=e.pay;
  // 工时统计显示申请工时；工资按财务核定的年假工时（没核定时等于申请工时）
  const annualPay=p.annualApproved!=null?Math.min(p.annualApproved,annual):annual;
  const leave=r2(sick+annual+ph+cash), leavePay=r2(sick+annualPay+ph+cash);
  const H={leave:leavePay,wd:r2(by.wd/60),sat:r2(by.sat/60),sun:r2(by.sun/60),ph:r2(by.ph/60)};
  const total=r2(H.leave+H.wd+H.sat+H.sun+H.ph), weeks=(to-from)/7;
  const cap=p.mode==='transfer'?Infinity:p.mode==='cash'?0:p.fixedPerWeek*weeks;
  const rk=k=>k==='leave'?'wd':k;              // 带薪假用平日时薪
  let left=cap; const T={},C={};
  ORDER.forEach(k=>{const take=r2(Math.min(H[k],Math.max(0,left))); T[k]=take; C[k]=r2(H[k]-take); left-=take;});
  const amt=(B,R)=>R?ORDER.reduce((a,k)=>a+r2(B[k]*R[rk(k)]),0):0;
  const tH=r2(ORDER.reduce((a,k)=>a+T[k],0)), cH=r2(ORDER.reduce((a,k)=>a+C[k],0));
  // 现金单一时薪：现金工时合计 × 现金时薪
  const tAmt=r2(amt(T,p.t)), cAmt=p.c?r2(cH*p.c):0;
  return {cs,idx,paid,H,total,sick,annual,annualPay,ph,cash,leave,leavePay,pend,other,T,C,tH,cH,tAmt,cAmt,pay:r2(tAmt+cAmt+p.allow)};
}
