// 共用样例数据（取自用户截图的 HB038 商品）
window.ROWS = [
 {item:'HB038-158', bc:'9525810380036', name:'Pleated Paper Silk Four-Color Pack 50G', en:'', grade:'A', wcat:'手工 / 纸品', sup:'HB（200）', spec:'110 × 155 × 12', off:10, w:'95 g', hook:'双钩 200', q:15, c:'#ffe58f'},
 {item:'HB038-XM-017', bc:'9527903800090', name:'Xmas Shredded Crinkle Paper Red And White', en:'Xmas Shredded Crinkle Paper Red And White', grade:'B', wcat:'圣诞 / 装饰', sup:'HB（200）', spec:null, c:'#ffa39e'},
 {item:'HB038-146', bc:'9525810380024', name:'Linen Roll', en:'', grade:'C', wcat:'手工 / 布艺', sup:'HB（200）', spec:'60 × 210 × 40', off:10, w:'120 g', hook:'双钩 200', q:4, c:'#d9d9d9'},
 {item:'HB038-XM-014', bc:'9525810380014', name:"X'MAS DECORATION 1CM Velvet Ribbon Hanging Card", en:'', grade:'A', wcat:'圣诞 / 丝带', sup:'HB（200）', spec:null, c:'#cf1322'},
 {item:'HB038-141', bc:'9525810380019', name:'Jade Thread Six-Color Card', en:'', grade:null, wcat:'手工 / 线材', sup:'HB（200）', spec:'85 × 180 × 8', off:10, w:'30 g', hook:'双钩 200', q:22, c:'#b5f5ec'},
 {item:'HB038-XM-002', bc:'9525810380002', name:'Xmas Shredded Crinkle Paper Red', en:'Xmas Shredded Crinkle Paper Red', grade:'B', wcat:'圣诞 / 装饰', sup:'HB（200）', spec:null, c:'#ff7875'},
 {item:'HB038-XM-007', bc:'9525810380007', name:'Xmas Pom Poms', en:'Xmas Pom Poms', grade:'D', wcat:'圣诞 / 装饰', sup:'HB（200）', spec:null, c:'#95de64'},
 {item:'HB038-150', bc:'9525810380031', name:'Glitter Glue 6pk', en:'', grade:'A', wcat:'手工 / 胶水', sup:'HB（200）', spec:'95 × 160 × 20', off:10, w:'110 g', hook:'双钩 200', q:9, c:'#d3adf7'},
 {item:'HB038-122', bc:'9525810380011', name:'Craft Pipe Cleaners 100pk', en:'', grade:'B', wcat:'手工 / 毛条', sup:'HB（200）', spec:null, c:'#91caff'},
 {item:'HB038-133', bc:'9525810380015', name:'Foam Stickers Assorted', en:'', grade:null, wcat:'手工 / 贴纸', sup:'HB（200）', spec:'120 × 170 × 6', off:10, w:'25 g', hook:'双钩 200', q:30, c:'#ffd6e7'},
];
window.GRADE = {A:['#722ED1','#f9f0ff','#d3adf7','A 核心'],B:['#1890FF','#e6f4ff','#91caff','B 关注'],C:['#FA8C16','#fff7e6','#ffd591','C 停产'],D:['#F5222D','#fff1f0','#ffa39e','D 清仓']};
window.gradeTag = (g, small) => g ? `<span class="tag" style="color:${GRADE[g][0]};background:${GRADE[g][1]};border-color:${GRADE[g][2]};${small?'height:18px;font-size:11px':''}">${small?g:GRADE[g][3]}</span>` : `<span class="tag gray" style="${small?'height:18px;font-size:11px':''}">未分级</span>`;
window.shell = (inner, crumbTail) => `
<div class="app" style="position:relative">
 <aside class="sider collapsed"><div class="brand" style="justify-content:center;padding:0"><div class="brand-mark">HB</div></div>
  <div class="menu" style="padding:8px 12px">${['layout-dashboard','settings','database','shopping-cart','gift','chart-bar','wallet'].map((i,k)=>`<div class="item ${k==6?'active':''}" style="padding:0;justify-content:center"><i class="ti ti-${i}"></i></div>`).join('')}</div></aside>
 <div class="main">
  <div class="header"><div class="crumb"><i class="ti ti-layout-sidebar-left-expand" style="margin-right:14px;color:rgba(0,0,0,.65)"></i>收银管理<span class="sep">/</span>商品与价格<span class="sep">/</span><b>${crumbTail||'商品陈列图'}</b></div>
   <div class="hdr-right"><span class="seg"><span class="on">中文</span><span>EN</span></span><span class="btn primary"><i class="ti ti-shopping-bag"></i>订货前台</span><span class="avatar"><i class="ti ti-user"></i></span><div style="line-height:1.2"><div style="font-weight:600;font-size:13px">admin</div><div class="muted tiny">Admin</div></div></div></div>
  <div class="tabs"><span class="tab"><i class="ti ti-home"></i>工作台</span><span class="tab on">商品与价格 <i class="ti ti-x muted"></i></span></div>
  ${inner}
 </div>
</div>`;
window.subtabs = () => `<div style="display:flex;gap:28px;padding:0 4px;border-bottom:1px solid #f0f0f0;font-size:14px;color:rgba(0,0,0,.65)"><span style="padding:10px 0">商品管理</span><span style="padding:10px 0">分店商品价格</span><span style="padding:10px 0">自动价格策略</span><span style="padding:10px 0">促销管理</span><span style="padding:10px 0;color:#1677ff;border-bottom:2px solid #1677ff;font-weight:500">商品陈列图</span></div>`;
window.innerTabs = () => `<div style="display:flex;gap:28px;padding:0 16px;border-bottom:1px solid #f0f0f0;font-size:14px;color:rgba(0,0,0,.65)"><span style="padding:12px 0">陈列方案</span><span style="padding:12px 0;color:#1677ff;border-bottom:2px solid #1677ff;font-weight:500">商品陈列参数</span><span style="padding:12px 0">货架与挂钩型号</span></div>`;
window.thumb = (r, s) => `<div class="thumb" style="width:${s}px;height:${s}px;background:${r.c};opacity:.85"></div>`;
window.specCells = (r) => r.spec ? `<td>${r.spec} mm</td><td>${r.off} mm</td><td>${r.w}</td><td>${r.hook}</td><td>${r.q} <span class="muted tiny">自动</span></td>` : `<td colspan="5" style="color:#d46b08"><i class="ti ti-alert-circle"></i> 未设陈列参数 · 设计器里不能使用</td>`;
