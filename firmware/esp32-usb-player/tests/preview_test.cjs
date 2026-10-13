const assert=require('node:assert/strict');
const {parseMidi,PreviewSynth}=require('../web/preview.js');
const vlq=n=>{let a=[n&127];while(n>>=7)a.unshift((n&127)|128);return a;};
const ev=(t,...b)=>[...vlq(t),...b];
const track=events=>{const b=Buffer.from(events.flat());return Buffer.concat([Buffer.from('MTrk'),Buffer.from([b.length>>>24,b.length>>>16&255,b.length>>>8&255,b.length&255]),b]);};
const midi=tracks=>Buffer.concat([Buffer.from([77,84,104,100,0,0,0,6,0,tracks.length===1?0:1,0,tracks.length,1,224]),...tracks.map(track)]);
const end=ev(0,255,47,0);
const tempo=n=>ev(0,255,81,3,n>>16,n>>8&255,n&255);
let data=midi([[tempo(1000000),ev(0,144,60,100),ev(480,128,60,0),end]]);
let song=parseMidi(data);assert.equal(song.duration,1000);assert.deepEqual(song.segments,[{start:0,end:1000,pitch:60,velocity:100}]);
// Tempo on the conductor track, leading trim, running status and tempo change.
data=midi([[tempo(500000),ev(480,255,81,3,15,66,64),end],[ev(240,144,60,80),ev(240,60,0),ev(0,62,90),ev(480,62,0),end]]);
song=parseMidi(data);assert.equal(song.duration,1250);assert.deepEqual(song.segments.map(n=>[n.start,n.end,n.pitch]),[[0,250,60],[250,1250,62]]);assert.equal(song.track,1);
// Chord reduction, repeat note, and pedal: select the highest held pitch.
data=midi([[tempo(500000),ev(0,144,60,80),ev(0,144,64,100),ev(120,176,64,127),ev(120,128,64,0),ev(0,128,60,0),ev(240,176,64,0),end]]);
song=parseMidi(data);assert.equal(song.segments.length,1);assert.equal(song.segments[0].pitch,64);assert.equal(song.segments[0].end,500);
// Percussion cannot win melody selection.
data=midi([[ev(0,153,80,90),ev(0,144,67,80),ev(480,137,80,0),ev(0,128,67,0),end]]);assert.equal(parseMidi(data).channel,0);
for(const bad of [data.subarray(0,12),data.subarray(0,data.length-1),midi([[tempo(0),ev(0,144,60,90),end]])])assert.throws(()=>parseMidi(bad));
assert(parseMidi(Buffer.concat([data,Buffer.from([0,255,127,0])])));
let active=0,started=0,stopped=0,customWaves=0;
class Param{setValueAtTime(){}linearRampToValueAtTime(){}setTargetAtTime(){}}
class Context{
 constructor(){this.state='running';this.currentTime=0;this.sampleRate=48000;this.destination={};}
 async resume(){this.state='running';}
 createGain(){return {gain:new Param(),connect(){},disconnect(){}};}
 createPeriodicWave(){customWaves++;return {};}
 createBiquadFilter(){return {frequency:{},Q:{},connect(){},disconnect(){}};}
 createOscillator(){return {frequency:{},detune:new Param(),connect(){},disconnect(){},setPeriodicWave(){},start(){active++;started++;},stop(){stopped++;}};}
}
global.AudioContext=Context;
(async()=>{
 const synth=new PreviewSynth();await synth.unlock();synth.load({duration:2000,segments:[{start:0,end:2000,pitch:69,velocity:100}]});
 await synth.play();assert(synth.playing);assert.equal(customWaves,1);assert.equal(started,2);synth.context.currentTime=.4;synth.pause();assert.equal(synth.position,400);assert(!synth.playing);assert.equal(synth.nodes.size,0);
 await synth.seek(1200);assert.equal(synth.position,1200);await synth.play();assert.equal(started,4);synth.stop();assert.equal(synth.elapsed(),0);assert.equal(synth.nodes.size,0);
 let release;synth.context.state='suspended';synth.context.resume=()=>new Promise(r=>{release=()=>{synth.context.state='running';r();};});
 const pending=synth.play();synth.stop();release();await pending;assert(!synth.playing);assert.equal(synth.nodes.size,0);
 console.log('MIDI tempo, tracks, running status, sustain, malformed input and preview pause/seek/cancellation passed.');
})().catch(e=>{console.error(e);process.exitCode=1;});
