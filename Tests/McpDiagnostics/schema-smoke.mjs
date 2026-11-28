import fs from 'node:fs';
import { createRequire } from 'node:module';
const root=process.argv[2];
const require=createRequire(root+'/McpServer/index.js');
const Ajv=require('ajv');
const ajv=new Ajv({allErrors:true,useDefaults:true,strict:false});
const tools=JSON.parse(fs.readFileSync(root+'/McpServer/tools.json','utf8')).tools;
const code=fs.readFileSync(root+'/Mcp/McpCommandExecutor.cs','utf8');
const names=['check_cable_routes','explain_cable_length','manage_clash_report','capture_clash_view','compare_clash_reports','check_line_consistency','compare_journal_with_model','run_project_checks'];
for(const name of names){
 const matches=tools.filter(t=>t.name===name); if(matches.length!==1)throw new Error('Duplicate/missing schema '+name);
 if(!code.includes('Register("'+name+'"'))throw new Error('Missing handler '+name);
 const tool=matches[0]; const validate=ajv.compile(tool.inputSchema);
 const sample={};
 if(name==='explain_cable_length'||name==='compare_journal_with_model')sample.system='aps';
 if(name==='compare_journal_with_model')sample.journalPath='test.xlsx';
 if(name==='manage_clash_report')sample.reportPaths=['report.xml'];
 if(name==='compare_clash_reports'){sample.beforePaths=['old.xml'];sample.afterPaths=['new.xml'];}
 if(!validate(sample))throw new Error(name+JSON.stringify(validate.errors));
 if(tool.inputSchema.properties.dryRun && sample.dryRun!==true)throw new Error('Unsafe default '+name);
 if(!tool.timeoutSeconds)throw new Error('Missing timeout '+name);
 if(validate({...sample,unknownArgument:true}))throw new Error('Unexpected arguments accepted '+name);
 if(['check_cable_routes','explain_cable_length','compare_clash_reports','check_line_consistency','compare_journal_with_model'].includes(name)&&tool.annotations.readOnlyHint!==true)throw new Error('Missing readOnlyHint '+name);
 console.log('PASS: '+name+' schema, handler, defaults, timeout and annotations');
}
