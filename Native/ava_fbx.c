#include "ufbx.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>
static ufbx_progress_result progress(void *user,const ufbx_progress *p) { return (double)(clock()-*(clock_t*)user)/CLOCKS_PER_SEC > 15.0 ? UFBX_PROGRESS_CANCEL : UFBX_PROGRESS_CONTINUE; }
#define API __declspec(dllexport)
#define LIMIT_VERTS 3000000
static ufbx_node *node(ufbx_scene *s, int n) { return s && n>=0 && (size_t)n<s->nodes.count ? s->nodes.data[n] : NULL; }
static ufbx_mesh *mesh(ufbx_scene *s,int n) { ufbx_node *p=node(s,n); return p?p->mesh:NULL; }
static ufbx_skin_deformer *skin(ufbx_mesh *m) { return m&&m->skin_deformers.count?m->skin_deformers.data[0]:NULL; }
static ufbx_scene *load_scene(const void *data, int size, char *error, int capacity, bool motion) {
 if(!data||size<=0||size>268435456) return NULL;
 ufbx_load_opts o={0}; ufbx_error e; clock_t started=clock(); o.progress_cb.fn=progress; o.progress_cb.user=&started; o.progress_interval_hint=65536;
 o.temp_allocator.memory_limit=536870912; o.result_allocator.memory_limit=536870912;
 o.temp_allocator.allocation_limit=1000000; o.result_allocator.allocation_limit=1000000;
 o.load_external_files=false; o.ignore_animation=!motion; o.ignore_geometry=motion; o.ignore_embedded=true;
 if (motion) {
  o.space_conversion=UFBX_SPACE_CONVERSION_MODIFY_GEOMETRY;
  o.handedness_conversion_axis=UFBX_MIRROR_AXIS_X;
 }
 o.file_format=UFBX_FILE_FORMAT_FBX;
 o.target_axes=ufbx_axes_left_handed_y_up; o.target_unit_meters=1.0;
 o.geometry_transform_handling=UFBX_GEOMETRY_TRANSFORM_HANDLING_HELPER_NODES;
 o.inherit_mode_handling=UFBX_INHERIT_MODE_HANDLING_HELPER_NODES;
 // Keep FBX face winding consistent with explicit normals. Unity handles mirrored node transforms.
 o.generate_missing_normals=true; o.reverse_winding=false;
 ufbx_scene *s=ufbx_load_memory(data,(size_t)size,&o,&e);
 if(!s) { if(error&&capacity>0) ufbx_format_error(error,(size_t)capacity,&e); return NULL; }
 size_t total=0;
 if(s->nodes.count>20000) { ufbx_free_scene(s); return NULL; }
 for(size_t i=0;i<s->meshes.count;i++) { ufbx_mesh *m=s->meshes.data[i]; total+=m->num_triangles*3;
  if(total>LIMIT_VERTS||m->max_face_triangles>100000||m->materials.count>256||(skin(m)&&skin(m)->clusters.count>1024)) { ufbx_free_scene(s); return NULL; }
 }
 total=0;
 for(size_t i=0;i<s->nodes.count;i++) { ufbx_node *n=s->nodes.data[i];if(n->mesh)total+=n->mesh->num_triangles*3;
  int depth=0;for(ufbx_node*p=n;p;p=p->parent)if(++depth>256){ufbx_free_scene(s);return NULL;}
  if(total>LIMIT_VERTS || n->materials.count>256){ufbx_free_scene(s);return NULL;}
 }
 return s;
}
API void *ava_load(const void *data, int size, char *error, int capacity) {
 return load_scene(data,size,error,capacity,false);
}
API void *ava_load_motion(const void *data, int size, char *error, int capacity) {
 return load_scene(data,size,error,capacity,true);
}
API int ava_take_range(ufbx_scene *s,double *begin,double *end) {
 if (!s || !s->anim_stacks.count || !s->anim_curves.count) return 0;
 const ufbx_anim_stack *take=s->anim_stacks.data[0];
 *begin=take->time_begin; *end=take->time_end;
 return 1;
}
// Sample the whole hierarchy together, including animation on non-humanoid parents.
API int ava_motion_pose(ufbx_scene *s,double time,bool rest,float *out,int count) {
 if (!s || !s->anim_stacks.count || (size_t)count!=s->nodes.count) return 0;
 const ufbx_anim *anim=s->anim_stacks.data[0]->anim;
 for (int i=0;i<count;i++) {
  const ufbx_node *n=s->nodes.data[i];
  ufbx_transform t=rest ? n->local_transform : ufbx_evaluate_transform(anim,n,time);
  float *v=out+10*i;
  for (int k=0;k<3;k++) { v[k]=(float)t.translation.v[k]; v[7+k]=(float)t.scale.v[k]; }
  for (int k=0;k<4;k++) v[3+k]=(float)t.rotation.v[k];
 }
 return 1;
}
API void ava_free(ufbx_scene *s) { ufbx_free_scene(s); }
API int ava_nodes(ufbx_scene *s) { return s?(int)s->nodes.count:0; }
API const char *ava_name(ufbx_scene *s,int n) { ufbx_node *p=node(s,n); return p?p->name.data:""; }
API int ava_parent(ufbx_scene *s,int n) { ufbx_node *p=node(s,n); return p&&p->parent?(int)p->parent->typed_id:-1; }
API void ava_transform(ufbx_scene *s,int n,float *out) { ufbx_node *p=node(s,n); if(!p)return; ufbx_transform t=p->local_transform;
 out[0]=(float)t.translation.x;out[1]=(float)t.translation.y;out[2]=(float)t.translation.z;
 out[3]=(float)t.rotation.x;out[4]=(float)t.rotation.y;out[5]=(float)t.rotation.z;out[6]=(float)t.rotation.w;
 out[7]=(float)t.scale.x;out[8]=(float)t.scale.y;out[9]=(float)t.scale.z;
}
API int ava_vertices(ufbx_scene *s,int n) { ufbx_mesh *m=mesh(s,n);return m?(int)(m->num_triangles*3):0; }
API int ava_materials(ufbx_scene *s,int n) { ufbx_node *p=node(s,n);return p?(int)p->materials.count:0; }
API const char *ava_material_name(ufbx_scene*s,int n,int i) { ufbx_node*p=node(s,n);return p&&i>=0&&(size_t)i<p->materials.count?p->materials.data[i]->name.data:"Default"; }
API int ava_bones(ufbx_scene *s,int n) { ufbx_skin_deformer*d=skin(mesh(s,n));return d?(int)d->clusters.count:0; }
API int ava_bone(ufbx_scene*s,int n,int b,float*out) { ufbx_skin_deformer*d=skin(mesh(s,n));if(!d||b<0||(size_t)b>=d->clusters.count)return -1;
 ufbx_skin_cluster*c=d->clusters.data[b];for(int i=0;i<12;i++)out[i]=(float)c->geometry_to_bone.v[i];return c->bone_node?(int)c->bone_node->typed_id:-1;
}
// Interleaved vertex: position3, normal3, uv2, weights4. Bone IDs separate; triangle materials one per vertex.
API int ava_mesh_with_indices(ufbx_scene*s,int n,float*out,int*bones,int*mats,int*control_points,int capacity) {
 ufbx_mesh*m=mesh(s,n);if(!m||capacity<(int)(m->num_triangles*3))return 0;
 uint32_t *ix=malloc(sizeof(uint32_t)*m->max_face_triangles*3);if(!ix)return 0;
 ufbx_skin_deformer*d=skin(m);int dst=0;
 for(size_t fi=0;fi<m->faces.count;fi++) { ufbx_face f=m->faces.data[fi];uint32_t count=ufbx_triangulate_face(ix,m->max_face_triangles*3,m,f)*3;
  for(uint32_t j=0;j<count;j++) { uint32_t k=ix[j];ufbx_vec3 p=ufbx_get_vertex_vec3(&m->vertex_position,k), normal=ufbx_get_vertex_vec3(&m->vertex_normal,k);ufbx_vec2 uv={0};if(m->vertex_uv.exists)uv=ufbx_get_vertex_vec2(&m->vertex_uv,k);
   float*v=out+dst*12;v[0]=(float)p.x;v[1]=(float)p.y;v[2]=(float)p.z;v[3]=(float)normal.x;v[4]=(float)normal.y;v[5]=(float)normal.z;v[6]=(float)uv.x;v[7]=(float)uv.y;
   for(int a=0;a<4;a++){v[8+a]=0;bones[dst*4+a]=0;}float sum=0;
   uint32_t vi=m->vertex_indices.data[k];if(control_points)control_points[dst]=(int)vi;if(d&&vi<d->vertices.count){ufbx_skin_vertex sv=d->vertices.data[vi];for(uint32_t a=0;a<sv.num_weights&&a<4;a++){ufbx_skin_weight w=d->weights.data[sv.weight_begin+a];v[8+a]=(float)w.weight;bones[dst*4+a]=(int)w.cluster_index;sum+=(float)w.weight;}}
   if(sum>0)for(int a=0;a<4;a++)v[8+a]/=sum;
   mats[dst]=m->face_material.count>(size_t)fi?(int)m->face_material.data[fi]:0;dst++;
  }
 }
 free(ix);return dst;
}




API int ava_mesh(ufbx_scene*s,int n,float*out,int*bones,int*mats,int capacity) {
 return ava_mesh_with_indices(s,n,out,bones,mats,NULL,capacity);
}

// Flatten all blend deformers into the channel order presented to the editor.
static ufbx_blend_channel *blend_channel(ufbx_mesh *m,int index) {
 if(!m || index<0)return NULL;
 for(size_t i=0;i<m->blend_deformers.count;i++){
  ufbx_blend_channel_list channels=m->blend_deformers.data[i]->channels;
  if((size_t)index<channels.count)return channels.data[index];
  index-=(int)channels.count;
 }
 return NULL;
}
API int ava_blend_channels(ufbx_scene *s,int n) {
 ufbx_mesh *m=mesh(s,n);int count=0;
 if(m)for(size_t i=0;i<m->blend_deformers.count;i++)count+=(int)m->blend_deformers.data[i]->channels.count;
 return count;
}
API const char *ava_blend_name(ufbx_scene *s,int n,int channel) {
 ufbx_blend_channel *c=blend_channel(mesh(s,n),channel);return c?c->name.data:"";
}
API int ava_blend_frames(ufbx_scene *s,int n,int channel) {
 ufbx_blend_channel *c=blend_channel(mesh(s,n),channel);return c?(int)c->keyframes.count:0;
}
API float ava_blend_frame(ufbx_scene *s,int n,int channel,int frame,const int *control_points,int count,float *offsets) {
 ufbx_mesh *m=mesh(s,n);
 ufbx_blend_channel *c=blend_channel(m,channel);
 if(!c||frame<0||(size_t)frame>=c->keyframes.count||count!=(int)(m->num_triangles*3))return 0;
 const ufbx_blend_keyframe *key=&c->keyframes.data[frame];
 if(!key->shape)return 0;
 // The managed buffers contain triangle corners. Expand each sparse FBX target
 // once by control point instead of searching the shape for every corner.
 if(m->num_vertices>LIMIT_VERTS)return 0;
 ufbx_vec3 *by_vertex=calloc(m->num_vertices,sizeof(*by_vertex));
 if(!by_vertex)return 0;
 const ufbx_blend_shape *shape=key->shape;
 for(size_t i=0;i<shape->num_offsets;i++){
  uint32_t point=shape->offset_vertices.data[i];
  if(point<m->num_vertices)by_vertex[point]=shape->position_offsets.data[i];
 }
 for(int i=0;i<count;i++){
  uint32_t point=(uint32_t)control_points[i];
  ufbx_vec3 delta=point<m->num_vertices?by_vertex[point]:ufbx_zero_vec3;
  offsets[i*3]=(float)delta.x;offsets[i*3+1]=(float)delta.y;offsets[i*3+2]=(float)delta.z;
 }
 free(by_vertex);
 return (float)(key->target_weight*100.0);
}
