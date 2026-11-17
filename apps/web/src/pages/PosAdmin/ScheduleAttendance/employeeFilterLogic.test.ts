import assert from 'node:assert/strict'

import {
  buildEmployeeOptions,
  matchesEmployeeKeyword,
  resolveEmployeeCell,
  shortGuid,
  splitHighlight,
  toStaffNameSource,
} from './employeeFilterLogic'
import type { StaffNameSource } from './staffNameLogic'

const zhan: StaffNameSource = {
  userGuid: '993f731f-0de7-4741-8b55-6099c50a97fb', username: 'zhan.chen', fullName: 'Zhan Chen', givenName: 'Zhan', familyName: 'Chen', nickname: 'Ethan',
}
const hunter: StaffNameSource = {
  userGuid: '52fb7075-355f-4054-948e-3df3b2aeefef', username: 'hunter.lj', fullName: 'Hunter Lehmann Jones', givenName: 'Hunter', familyName: 'Jones',
}
const cody: StaffNameSource = {
  userGuid: 'd5658511-d106-48f4-8ae9-c961a83e666f', username: 'cody.j', fullName: 'Cody Johnson', givenName: 'Cody', familyName: 'Johnson',
}
const pei: StaffNameSource = {
  userGuid: '5899295b-a378-4377-953a-ee3717850e75', username: 'peiyi.z', fullName: 'Peiyi Zhang', givenName: 'Peiyi', familyName: 'Zhang', nickname: 'Pei',
}

// ---------- toStaffNameSource：只搬运显示名需要的字段 ----------
assert.deepEqual(
  toStaffNameSource({ userGuid: 'g', username: 'u', fullName: 'F N', givenName: 'F', familyName: 'N', nickname: 'Nick', roleNames: ['x'] } as never),
  { userGuid: 'g', username: 'u', fullName: 'F N', givenName: 'F', familyName: 'N', nickname: 'Nick' },
)

// ---------- 下拉选项：昵称模式 ----------
{
  const options = buildEmployeeOptions([zhan, hunter, cody, pei], 'nickname')
  // 主文字昵称（没填用名兜底），按显示名排序
  assert.deepEqual(options.map((option) => option.label), ['Cody', 'Ethan', 'Hunter', 'Pei'])
  const ethan = options.find((option) => option.value === zhan.userGuid)
  assert.equal(ethan?.secondary, 'Zhan Chen', '右侧灰字是全名')
  assert.equal(options.find((option) => option.label === 'Hunter')?.secondary, 'Hunter Lehmann Jones', '没填昵称：主文字是名，灰字是全名')
}

// ---------- 下拉选项：全名模式 ----------
{
  const options = buildEmployeeOptions([zhan, hunter, cody, pei], 'full')
  assert.deepEqual(options.map((option) => option.label), ['Cody Johnson', 'Hunter Lehmann Jones', 'Peiyi Zhang', 'Zhan Chen'])
  assert.equal(options.find((option) => option.value === zhan.userGuid)?.secondary, 'Ethan', '全名模式灰字是昵称')
  assert.equal(options.find((option) => option.value === pei.userGuid)?.secondary, 'Pei')
}

// ---------- 重名：沿用看板规则（补姓的首字母），灰字给出全名 ----------
{
  const samChen: StaffNameSource = { userGuid: 'a', username: 'sam.chen', fullName: 'Sam Chen', givenName: 'Sam', familyName: 'Chen', nickname: 'Sam' }
  const samLee: StaffNameSource = { userGuid: 'b', username: 'sam.lee', fullName: 'Sam Lee', givenName: 'Sam', familyName: 'Lee', nickname: 'Sam' }
  const options = buildEmployeeOptions([samLee, samChen], 'nickname')
  assert.deepEqual(options.map((option) => [option.label, option.secondary]), [['Sam C.', 'Sam Chen'], ['Sam L.', 'Sam Lee']])
}

// ---------- 昵称和全名恰好相同：灰字退回登录名，右侧不留空 ----------
{
  const single: StaffNameSource = { userGuid: 'c', username: 'madonna', fullName: 'Madonna' }
  const [option] = buildEmployeeOptions([single], 'nickname')
  assert.equal(option.label, 'Madonna')
  assert.equal(option.secondary, 'madonna')
}

// ---------- 搜索：昵称 / 名 / 全名 / 登录名 / GUID / 混合写法都能找到，换了开关也一样 ----------
{
  for (const mode of ['nickname', 'full'] as const) {
    const options = buildEmployeeOptions([zhan, hunter, cody, pei], mode)
    const find = (keyword: string) => options.filter((option) => matchesEmployeeKeyword(option.search, keyword)).map((option) => option.value)
    assert.deepEqual(find('zh'), [pei.userGuid, zhan.userGuid].sort((a, b) => {
      const label = (guid: string) => options.find((option) => option.value === guid)?.label ?? ''
      return label(a).localeCompare(label(b), undefined, { sensitivity: 'base' })
    }), `${mode}：zh 同时命中 Zhan Chen 和 Peiyi Zhang`)
    assert.deepEqual(find('ETHAN'), [zhan.userGuid], `${mode}：昵称，忽略大小写`)
    assert.deepEqual(find('  hunter.lj '), [hunter.userGuid], `${mode}：登录名，忽略首尾空格`)
    assert.deepEqual(find('3df3b2aeefef'), [hunter.userGuid], `${mode}：GUID 片段`)
    assert.deepEqual(find('ethan chen'), [zhan.userGuid], `${mode}：昵称 + 姓的混合写法`)
    assert.deepEqual(find('peiyi zhang'), [pei.userGuid], `${mode}：名 + 姓`)
    assert.equal(find('nobody').length, 0)
    assert.equal(find('').length, 4, `${mode}：空关键字全部命中`)
  }
  assert.equal(matchesEmployeeKeyword(undefined, 'a'), false)
  assert.equal(matchesEmployeeKeyword(undefined, ''), true)
}

// ---------- 高亮切片 ----------
assert.deepEqual(splitHighlight('Zhan Chen', 'zh'), [{ text: 'Zh', match: true }, { text: 'an Chen', match: false }])
assert.deepEqual(splitHighlight('Peiyi Zhang', 'ZH'), [{ text: 'Peiyi ', match: false }, { text: 'Zh', match: true }, { text: 'ang', match: false }])
assert.deepEqual(splitHighlight('Ethan', ''), [{ text: 'Ethan', match: false }])
assert.deepEqual(splitHighlight('Ethan', '  '), [{ text: 'Ethan', match: false }])
assert.deepEqual(splitHighlight('Ethan', 'xyz'), [{ text: 'Ethan', match: false }])
// 关键字里的正则符号按普通字符处理，不能抛错
assert.deepEqual(splitHighlight('Sam (sam.chen)', '(sam'), [{ text: 'Sam ', match: false }, { text: '(sam', match: true }, { text: '.chen)', match: false }])
assert.deepEqual(splitHighlight('a.b', '.'), [{ text: 'a', match: false }, { text: '.', match: true }, { text: 'b', match: false }])
assert.deepEqual(splitHighlight('', 'a'), [{ text: '', match: false }])

// ---------- 员工列：名单里的人 ----------
assert.deepEqual(resolveEmployeeCell({ userGuid: zhan.userGuid, mode: 'nickname', source: zhan }), {
  primary: 'Ethan', alt: { kind: 'full', name: 'Zhan Chen' }, unresolved: false,
})
assert.deepEqual(resolveEmployeeCell({ userGuid: zhan.userGuid, mode: 'full', source: zhan }), {
  primary: 'Zhan Chen', alt: { kind: 'nickname', name: 'Ethan' }, unresolved: false,
})
// 没填昵称：昵称模式用名兜底；全名模式第二行补「昵称」叫法（也就是名）
assert.deepEqual(resolveEmployeeCell({ userGuid: hunter.userGuid, mode: 'nickname', source: hunter }), {
  primary: 'Hunter', alt: { kind: 'full', name: 'Hunter Lehmann Jones' }, unresolved: false,
})
assert.deepEqual(resolveEmployeeCell({ userGuid: hunter.userGuid, mode: 'full', source: hunter }), {
  primary: 'Hunter Lehmann Jones', alt: { kind: 'nickname', name: 'Hunter' }, unresolved: false,
})
// 两种叫法相同：只剩一行
{
  const single: StaffNameSource = { userGuid: 'c', username: 'madonna', fullName: 'Madonna' }
  assert.deepEqual(resolveEmployeeCell({ userGuid: 'c', mode: 'nickname', source: single }), { primary: 'Madonna', alt: undefined, unresolved: false })
  assert.deepEqual(resolveEmployeeCell({ userGuid: 'c', mode: 'full', source: single }), { primary: 'Madonna', alt: undefined, unresolved: false })
}
// 重名：昵称模式用已区分的显示名（Sam C.），不是裸的 Sam
assert.deepEqual(
  resolveEmployeeCell({
    userGuid: 'a', mode: 'nickname', nicknameName: 'Sam C.',
    source: { userGuid: 'a', username: 'sam.chen', fullName: 'Sam Chen', givenName: 'Sam', familyName: 'Chen', nickname: 'Sam' },
  }),
  { primary: 'Sam C.', alt: { kind: 'full', name: 'Sam Chen' }, unresolved: false },
)

// ---------- 员工列：不在名单里的人 ----------
// 考勤记录接口自带全名：昵称模式也只能显示全名，不重复第二行
assert.deepEqual(resolveEmployeeCell({ userGuid: 'x', mode: 'nickname', recordName: ' Old Staff ' }), { primary: 'Old Staff', alt: undefined, unresolved: false })
assert.deepEqual(resolveEmployeeCell({ userGuid: 'x', mode: 'full', recordName: 'Old Staff' }), { primary: 'Old Staff', alt: undefined, unresolved: false })
// 打卡接口不返回姓名、名单里也没有：显示 GUID 前 8 位
assert.deepEqual(resolveEmployeeCell({ userGuid: '8f3a41c2-1111-2222-3333-444455556666', mode: 'nickname' }), { primary: '8f3a41c2', unresolved: true })
assert.deepEqual(resolveEmployeeCell({ userGuid: '8f3a41c2-1111-2222-3333-444455556666', mode: 'full', recordName: '   ' }), { primary: '8f3a41c2', unresolved: true })
// 名单里有这个人但连名字、登录名都没填：同样退回 GUID 前 8 位
assert.deepEqual(resolveEmployeeCell({ userGuid: 'abcdef12-0000', mode: 'nickname', source: { userGuid: 'abcdef12-0000' } }), { primary: 'abcdef12', unresolved: true })
// 只有登录名也算取到了名字
assert.deepEqual(resolveEmployeeCell({ userGuid: 'g1', mode: 'nickname', source: { userGuid: 'g1', username: 'hbstaff07' } }), { primary: 'hbstaff07', alt: undefined, unresolved: false })

assert.equal(shortGuid('993f731f-0de7-4741-8b55-6099c50a97fb'), '993f731f')
assert.equal(shortGuid('abc'), 'abc')

console.log('employeeFilterLogic tests passed')
